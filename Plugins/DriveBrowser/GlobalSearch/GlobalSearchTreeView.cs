using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DriveBrowser
{
	public class GlobalSearchTreeView : TreeView
	{
		private readonly List<DriveFile> searchResults;

		private readonly CompareInfo textComparer;
		private readonly CompareOptions textCompareOptions;

		private readonly System.Action<DriveFile[]> onFilesRightClicked;

		private readonly GUIContent sharedGUIContent = new GUIContent();

		public GlobalSearchTreeView( TreeViewState treeViewState, MultiColumnHeader header, List<DriveFile> searchResults, System.Action<DriveFile[]> onFilesRightClicked ) : base( treeViewState, header )
		{
			this.searchResults = searchResults;
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
			List<TreeViewItem> rows = ( GetRows() as List<TreeViewItem> ) ?? new List<TreeViewItem>( 64 );
			rows.Clear();

			bool isSearching = !string.IsNullOrEmpty( searchString );

			for( int i = 0; i < searchResults.Count; i++ )
			{
				DriveFile file = searchResults[i];
				if( !isSearching || textComparer.IndexOf( file.name, searchString, textCompareOptions ) >= 0 )
					rows.Add( new TreeViewItem( i, 0, file.name ) );
			}

			if( rows.Count > 0 )
			{
				rows.Sort( ( r1, r2 ) =>
				{
					DriveFile f1 = searchResults[r1.id];
					DriveFile f2 = searchResults[r2.id];

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

				foreach( TreeViewItem row in rows )
					root.AddChild( row );
			}
			else if( root.children == null ) // Otherwise: "InvalidOperationException: TreeView: 'rootItem.children == null'"
				root.children = new List<TreeViewItem>( 0 );

			return rows;
		}

		protected override void RowGUI( RowGUIArgs args )
		{
			DriveFile file = searchResults[args.item.id];

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
				fileIDs[i] = searchResults[sortedItemIDs[i]].id;

			HelperFunctions.InitiateDragDropDownload( fileIDs );
		}

		protected override void ContextClickedItem( int id )
		{
			IList<int> selection = GetSelection();
			if( selection == null || selection.Count == 0 )
				return;

			DriveFile[] selectedFiles = new DriveFile[selection.Count];
			for( int i = 0; i < selection.Count; i++ )
				selectedFiles[i] = searchResults[selection[i]];

			onFilesRightClicked?.Invoke( selectedFiles );
		}
	}
}