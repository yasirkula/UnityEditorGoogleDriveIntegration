using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DriveBrowser
{
	public class FilesTreeView : TreeView
	{
		private readonly DriveFile rootFolder;

		// We use a unique string's hash code as id, so the id we use here isn't really guaranteed to be unique. Fingers crossed :|
		private readonly Dictionary<int, DriveFile> fileIDToFile = new Dictionary<int, DriveFile>( 1024 );

		private readonly System.Action<DriveFile> onFolderExplored;
		private readonly System.Action<DriveFile[]> onFilesRightClicked;

		private readonly CompareInfo textComparer;
		private readonly CompareOptions textCompareOptions;

		private readonly GUIContent sharedGUIContent = new GUIContent();

		public FilesTreeView( TreeViewState treeViewState, MultiColumnHeader header, DriveFile rootFolder, System.Action<DriveFile> onFolderExplored, System.Action<DriveFile[]> onFilesRightClicked ) : base( treeViewState, header )
		{
			this.rootFolder = rootFolder;

			this.onFolderExplored = onFolderExplored;
			this.onFilesRightClicked = onFilesRightClicked;

			textComparer = new CultureInfo( "en-US" ).CompareInfo;
			textCompareOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

			showBorder = true;

			header.visibleColumnsChanged += ( _header ) => _header.ResizeToFit();
			header.sortingChanged += ( _header ) =>
			{
				Reload();

				if( HasSelection() )
					SetSelection( GetSelection(), TreeViewSelectionOptions.RevealAndFrame );
			};

			Reload();
		}

		protected override TreeViewItem BuildRoot()
		{
			return new TreeViewItem { id = 0, depth = -1 };
		}

		protected override IList<TreeViewItem> BuildRows( TreeViewItem root )
		{
			List<TreeViewItem> rows = ( GetRows() as List<TreeViewItem> ) ?? new List<TreeViewItem>( 256 );
			rows.Clear();

			if( !string.IsNullOrEmpty( searchString ) )
				root.children = new List<TreeViewItem>( 256 );

			AddChildrenRecursive( rootFolder, root );
			SortRowsRecursive( root.children, rows );
			SetupDepthsFromParentsAndChildren( root );

			return rows;
		}

		private void AddChildrenRecursive( DriveFile folder, TreeViewItem item )
		{
			bool isSearching = !string.IsNullOrEmpty( searchString );
			if( !isSearching ) // While searching, all files are added to the root item and its children is initialized in BuildRows
				item.children = new List<TreeViewItem>( folder.children.Length );

			for( int i = 0; i < folder.children.Length; i++ )
			{
				DriveFile file = DriveAPI.GetFileByID( folder.children[i] );
				int id = file.id.GetHashCode();
				fileIDToFile[id] = file;

				if( !isSearching )
				{
					TreeViewItem childItem = new TreeViewItem( id, -1, file.name );
					item.AddChild( childItem );

					if( file.children.Length > 0 && IsExpanded( id ) )
						AddChildrenRecursive( file, childItem );
				}
				else
				{
					if( textComparer.IndexOf( file.name, searchString, textCompareOptions ) >= 0 )
					{
						TreeViewItem childItem = new TreeViewItem( id, -1, file.name );
						item.AddChild( childItem );
					}

					if( file.children.Length > 0 )
						AddChildrenRecursive( file, item );
				}
			}
		}

		public void Refresh( DriveFile refreshedFile )
		{
			// SetExpanded internally rebuilds the TreeView so there is no need to call SetExpanded and Reload together
			if( !IsExpanded( refreshedFile.id.GetHashCode() ) )
				SetExpanded( refreshedFile.id.GetHashCode(), true );
			else
				Reload();
		}

		protected override void RowGUI( RowGUIArgs args )
		{
			DriveFile file = fileIDToFile[args.item.id];

			if( Event.current.type == EventType.MouseDown && args.rowRect.Contains( Event.current.mousePosition ) )
			{
				Rect previewSourceRect = args.rowRect;
				previewSourceRect.width = EditorGUIUtility.currentViewWidth;

				FilePreviewPopup.Show( previewSourceRect, file );
			}

			for( int i = 0; i < args.GetNumVisibleColumns(); ++i )
			{
				Rect cellRect = args.GetCellRect( i );
				switch( args.GetColumn( i ) )
				{
					case 0: // Filename
					{
						cellRect.xMin += GetContentIndent( args.item );
						cellRect.y -= 2f;
						cellRect.height += 4f; // Incrementing height fixes cropped icon issue on Unity 2019.2 or earlier

						// Draw foldout arrow
						if( file.childrenState != FolderChildrenState.NoChildren && string.IsNullOrEmpty( searchString ) )
						{
							Rect foldoutRect = new Rect( cellRect.x - foldoutWidth, cellRect.center.y - EditorGUIUtility.singleLineHeight * 0.5f, foldoutWidth, EditorGUIUtility.singleLineHeight );

							// Folders that aren't explored yet will have green foldout arrows
							Color backgroundColor = GUI.backgroundColor;
							if( file.childrenState == FolderChildrenState.Unknown )
							{
								if( EditorGUIUtility.isProSkin )
									GUI.backgroundColor = Color.green;
								else // Foldout arrows don't turn green in light skin, we can show a green background icon instead
									GUI.DrawTexture( foldoutRect, EditorGUIUtility.Load( "d_greenLight" ) as Texture, ScaleMode.ScaleToFit );
							}

							EditorGUI.BeginChangeCheck();
							bool isExpanded = EditorGUI.Foldout( foldoutRect, file.childrenState != FolderChildrenState.Unknown && IsExpanded( args.item.id ), GUIContent.none );
							if( EditorGUI.EndChangeCheck() )
							{
								if( !isExpanded || file.childrenState == FolderChildrenState.HasChildren )
									SetExpanded( args.item.id, isExpanded );
								else
									onFolderExplored?.Invoke( file );
							}

							GUI.backgroundColor = backgroundColor;
						}

						sharedGUIContent.text = file.name;
						sharedGUIContent.image = file.isFolder ? AssetDatabase.GetCachedIcon( "Assets" ) as Texture2D : UnityEditorInternal.InternalEditorUtility.GetIconForFile( file.name );
						sharedGUIContent.tooltip = file.name;

						GUI.Label( cellRect, sharedGUIContent );
						break;
					}
					case 1: // File size
					{
						if( !file.isFolder )
							GUI.Label( cellRect, EditorUtility.FormatBytes( file.size ) );

						break;
					}
					case 2: // Last modified date
					{
						GUI.Label( cellRect, file.modifiedTime.ToString( "dd.MM.yy HH:mm" ) );
						break;
					}
				}
			}
		}

		private void SortRowsRecursive( List<TreeViewItem> rows, List<TreeViewItem> flattenedRows )
		{
			if( rows == null || rows.Count == 0 )
				return;

			if( rows.Count > 1 && multiColumnHeader.sortedColumnIndex != -1 )
			{
				rows.Sort( ( r1, r2 ) =>
				{
					DriveFile f1 = fileIDToFile[r1.id];
					DriveFile f2 = fileIDToFile[r2.id];

					switch( multiColumnHeader.sortedColumnIndex )
					{
						case 0: // Sort by name
						{
							if( f1.isFolder && !f2.isFolder )
								return -1;
							else if( !f1.isFolder && f2.isFolder )
								return 1;

							return f1.name.CompareTo( f2.name );
						}
						case 1: // Sort by file size
						{
							if( f1.isFolder && !f2.isFolder )
								return -1;
							else if( !f1.isFolder && f2.isFolder )
								return 1;

							return f1.size.CompareTo( f2.size );
						}
						case 2: // Sort by last modified date
						{
							return f1.modifiedTimeTicks.CompareTo( f2.modifiedTimeTicks );
						}
						default: return 0;
					}
				} );

				if( !multiColumnHeader.IsSortedAscending( multiColumnHeader.sortedColumnIndex ) )
					rows.Reverse();
			}

			foreach( TreeViewItem childRow in rows )
			{
				flattenedRows.Add( childRow );
				SortRowsRecursive( childRow.children, flattenedRows );
			}
		}

		protected override bool CanStartDrag( CanStartDragArgs args )
		{
			return true;
		}

		protected override void SetupDragAndDrop( SetupDragAndDropArgs args )
		{
			IList<int> sortedItemIDs = SortItemIDsInRowOrder( args.draggedItemIDs );
			if( sortedItemIDs.Count == 0 )
				return;

			string[] fileIDs = new string[sortedItemIDs.Count];
			for( int i = 0; i < sortedItemIDs.Count; i++ )
				fileIDs[i] = fileIDToFile[sortedItemIDs[i]].id;

			HelperFunctions.InitiateDragDropDownload( fileIDs );
		}

		protected override void ContextClickedItem( int id )
		{
			IList<int> selection = GetSelection();
			if( selection == null || selection.Count == 0 )
				return;

			DriveFile[] selectedFiles = new DriveFile[selection.Count];
			for( int i = 0; i < selection.Count; i++ )
				selectedFiles[i] = fileIDToFile[selection[i]];

			onFilesRightClicked?.Invoke( selectedFiles );
		}

		protected override bool CanChangeExpandedState( TreeViewItem item )
		{
			return false; // We draw the foldout arrow manually inside RowGUI
		}

		protected override IList<int> GetDescendantsThatHaveChildren( int id )
		{
			return new int[1] { id };
		}

		protected override IList<int> GetAncestors( int id )
		{
			if( !fileIDToFile.TryGetValue( id, out DriveFile file ) )
				return new int[1] { id };

			List<int> result = new List<int>( 4 ) { id };
			while( !string.IsNullOrEmpty( file.parentID ) )
			{
				int _parentID = file.parentID.GetHashCode();
				result.Add( _parentID );
				file = fileIDToFile[_parentID];
			}

			return result;
		}
	}
}