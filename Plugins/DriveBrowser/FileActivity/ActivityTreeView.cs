using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DriveBrowser
{
	public class ActivityTreeView : TreeView
	{
		private readonly List<ActivityEntry> activity;

		private bool showCreateEntries = true, showDeleteEntries = true, showEditEntries = true, showMoveEntries = true, showRenameEntries = true, showRestoreEntries = true;

		private readonly CompareInfo textComparer;
		private readonly CompareOptions textCompareOptions;

		private readonly System.Action<ActivityEntry[]> onEntriesRightClicked;

		private readonly GUIContent sharedGUIContent = new GUIContent();

		public ActivityTreeView( TreeViewState treeViewState, MultiColumnHeader header, List<ActivityEntry> activity, System.Action<ActivityEntry[]> onEntriesRightClicked ) : base( treeViewState, header )
		{
			this.activity = activity;
			this.onEntriesRightClicked = onEntriesRightClicked;

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
		}

		public void SetFilters( bool showCreateEntries, bool showDeleteEntries, bool showEditEntries, bool showMoveEntries, bool showRenameEntries, bool showRestoreEntries )
		{
			this.showCreateEntries = showCreateEntries;
			this.showDeleteEntries = showDeleteEntries;
			this.showEditEntries = showEditEntries;
			this.showMoveEntries = showMoveEntries;
			this.showRenameEntries = showRenameEntries;
			this.showRestoreEntries = showRestoreEntries;

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

			for( int i = 0; i < activity.Count; i++ )
			{
				ActivityEntry entry = activity[i];
				if( !isSearching || textComparer.IndexOf( entry.relativePath, searchString, textCompareOptions ) >= 0 || textComparer.IndexOf( entry.username, searchString, textCompareOptions ) >= 0 || textComparer.IndexOf( entry.type.ToString(), searchString, textCompareOptions ) >= 0 )
				{
					switch( entry.type )
					{
						case FileActivityType.Create: if( !showCreateEntries ) continue; break;
						case FileActivityType.Delete: if( !showDeleteEntries ) continue; break;
						case FileActivityType.Edit: if( !showEditEntries ) continue; break;
						case FileActivityType.Move: if( !showMoveEntries ) continue; break;
						case FileActivityType.Rename: if( !showRenameEntries ) continue; break;
						case FileActivityType.Restore: if( !showRestoreEntries ) continue; break;
					}

					rows.Add( new TreeViewItem( i, 0, entry.relativePath ) );
				}
			}

			if( rows.Count > 0 )
			{
				bool descendingSort = !multiColumnHeader.IsSortedAscending( multiColumnHeader.sortedColumnIndex );

				rows.Sort( ( r1, r2 ) =>
				{
					ActivityEntry f1 = activity[r1.id];
					ActivityEntry f2 = activity[r2.id];

					switch( multiColumnHeader.sortedColumnIndex )
					{
						case 0: // Sort by type
						{
							int result = f1.type.CompareTo( f2.type );
							return ( result != 0 ) ? result : ( descendingSort ? f1.timeTicks.CompareTo( f2.timeTicks ) : f2.timeTicks.CompareTo( f1.timeTicks ) );
						}
						case 1: // Sort by relative path
						{
							int result = f1.relativePath.CompareTo( f2.relativePath );
							return ( result != 0 ) ? result : ( descendingSort ? f1.timeTicks.CompareTo( f2.timeTicks ) : f2.timeTicks.CompareTo( f1.timeTicks ) );
						}
						case 2: // Sort by username
						{
							int result = f1.username.CompareTo( f2.username );
							return ( result != 0 ) ? result : ( descendingSort ? f1.timeTicks.CompareTo( f2.timeTicks ) : f2.timeTicks.CompareTo( f1.timeTicks ) );
						}
						case 3: // Sort by file size
						{
							int result = f1.size.CompareTo( f2.size );
							return ( result != 0 ) ? result : ( descendingSort ? f1.timeTicks.CompareTo( f2.timeTicks ) : f2.timeTicks.CompareTo( f1.timeTicks ) );
						}
						case 4: // Sort by last modified date
						{
							return f1.timeTicks.CompareTo( f2.timeTicks );
						}
						default: return 0;
					}
				} );

				if( descendingSort )
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
			ActivityEntry entry = activity[args.item.id];

			// Highlight permanently deleted files in red
			if( string.IsNullOrEmpty( entry.fileID ) )
				EditorGUI.DrawRect( args.rowRect, new Color( 1f, 0f, 0f, 0.2f ) );
			else if( Event.current.type == EventType.MouseDown && args.rowRect.Contains( Event.current.mousePosition ) )
			{
				Rect previewSourceRect = args.rowRect;
				previewSourceRect.width = EditorGUIUtility.currentViewWidth;

				FilePreviewPopup.Show( previewSourceRect, DriveAPI.GetFileByID( entry.fileID ) );
			}

			for( int i = 0; i < args.GetNumVisibleColumns(); ++i )
			{
				Rect cellRect = args.GetCellRect( i );
				switch( args.GetColumn( i ) )
				{
					case 0: // Activity type
					{
						GUI.Label( cellRect, entry.type.ToString() );
						break;
					}
					case 1: // Relative path
					{
						cellRect.y -= 2f;
						cellRect.height += 4f; // Incrementing height fixes cropped icon issue on Unity 2019.2 or earlier

						sharedGUIContent.text = entry.relativePath;
						sharedGUIContent.image = entry.isFolder ? AssetDatabase.GetCachedIcon( "Assets" ) as Texture2D : UnityEditorInternal.InternalEditorUtility.GetIconForFile( entry.relativePath );
						sharedGUIContent.tooltip = entry.relativePath;

						GUI.Label( cellRect, sharedGUIContent );
						break;
					}
					case 2: // Modified user
					{
						sharedGUIContent.text = entry.username;
						sharedGUIContent.image = null;
						sharedGUIContent.tooltip = entry.username;

						GUI.Label( cellRect, sharedGUIContent );
						break;
					}
					case 3: // File size
					{
						if( !entry.isFolder && entry.size >= 0L )
							GUI.Label( cellRect, EditorUtility.FormatBytes( entry.size ) );

						break;
					}
					case 4: // Modified time
					{
						GUI.Label( cellRect, entry.time.ToString( "dd.MM.yy HH:mm" ) );
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

			List<string> fileIDs = new List<string>( sortedItemIDs.Count );
			for( int i = 0; i < sortedItemIDs.Count; i++ )
			{
				string fileID = activity[sortedItemIDs[i]].fileID;

				// fileID can be null if this activity entry belongs to a permanently deleted file
				if( !string.IsNullOrEmpty( fileID ) )
					fileIDs.Add( fileID );
			}

			HelperFunctions.InitiateDragDropDownload( fileIDs.ToArray() );
		}

		protected override void ContextClickedItem( int id )
		{
			IList<int> selection = GetSelection();
			if( selection == null || selection.Count == 0 )
				return;

			ActivityEntry[] selectedEntries = new ActivityEntry[selection.Count];
			for( int i = 0; i < selection.Count; i++ )
				selectedEntries[i] = activity[selection[i]];

			onEntriesRightClicked?.Invoke( selectedEntries );
		}
	}
}