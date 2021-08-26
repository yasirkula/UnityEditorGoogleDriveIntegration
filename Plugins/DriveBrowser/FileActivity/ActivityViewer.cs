﻿using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DriveBrowser
{
	public class ActivityViewer : EditorWindow, IHasCustomMenu
	{
		private const int MINIMUM_ENTRY_COUNT_PER_FETCH = 20;

		private FileBrowser fileBrowser;
		private DriveFile inspectedFile;
		private string pageToken;

		private List<ActivityEntry> activity = new List<ActivityEntry>( 64 );

		private bool showCreateEntries = true, showDeleteEntries = true, showEditEntries = true, showMoveEntries = true, showRenameEntries = true, showRestoreEntries = true;

		private ActivityTreeView activityTreeView;
		private TreeViewState activityTreeViewState;
		private MultiColumnHeaderState activityTreeViewHeaderState;
		private SearchField searchField;

		private CancellationTokenSource cancellationTokenSource;

		private bool shouldRepositionSelf = true;

		private Vector2 scrollPos;

		private bool m_isBusy;
		private bool IsBusy
		{
			get { return m_isBusy; }
			set
			{
				if( m_isBusy != value )
				{
					m_isBusy = value;

					if( m_isBusy )
						HelperFunctions.LockAssemblyReload();
					else
						HelperFunctions.UnlockAssemblyReload();
				}
			}
		}

		public static ActivityViewer Initialize( FileBrowser fileBrowser, DriveFile inspectedFile )
		{
			ActivityViewer window = CreateInstance<ActivityViewer>();
#if UNITY_2019_3_OR_NEWER
			window.titleContent = new GUIContent( "File Activity", inspectedFile.isFolder ? AssetDatabase.GetCachedIcon( "Assets" ) as Texture2D : UnityEditorInternal.InternalEditorUtility.GetIconForFile( inspectedFile.name ) );
#else
			window.titleContent = new GUIContent( "File Activity" );
#endif
			window.minSize = new Vector2( 400f, 175f );

			window.fileBrowser = fileBrowser;
			window.inspectedFile = inspectedFile;

			window.Show();
			EditorApplication.delayCall += () => window.LoadMoreFileActivityAsync();

			return window;
		}

		private void OnEnable()
		{
			if( activityTreeViewState == null )
				activityTreeViewState = new TreeViewState();

			MultiColumnHeader multiColumnHeader = HelperFunctions.GenerateMultiColumnHeader( ref activityTreeViewHeaderState, 4,
				new HelperFunctions.HeaderColumn( "Type", false, true, 40f, 55f, 55f ),
				new HelperFunctions.HeaderColumn( "Name", true, true, 30f, 0f, 0f ),
				new HelperFunctions.HeaderColumn( "User", false, true, 30f, 0f, 70f ),
				new HelperFunctions.HeaderColumn( "Size", false, false, 30f, 0f, 70f ),
				new HelperFunctions.HeaderColumn( "Last Modified", false, false, 30f, 0f, 100f ) );

			activityTreeView = new ActivityTreeView( activityTreeViewState, multiColumnHeader, activity, OnEntriesRightClicked );
			activityTreeView.SetFilters( showCreateEntries, showDeleteEntries, showEditEntries, showMoveEntries, showRenameEntries, showRestoreEntries );

			searchField = new SearchField();
			searchField.downOrUpArrowKeyPressed += activityTreeView.SetFocusAndEnsureSelectedItem;
		}

		private void OnDisable()
		{
			if( cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested )
				cancellationTokenSource.Cancel();

			IsBusy = false;
		}

		void IHasCustomMenu.AddItemsToMenu( GenericMenu menu )
		{
			menu.AddItem( new GUIContent( "Show 'Create' Activity" ), showCreateEntries, () => ToggleFilter( ref showCreateEntries ) );
			menu.AddItem( new GUIContent( "Show 'Delete' Activity" ), showDeleteEntries, () => ToggleFilter( ref showDeleteEntries ) );
			menu.AddItem( new GUIContent( "Show 'Edit' Activity" ), showEditEntries, () => ToggleFilter( ref showEditEntries ) );
			menu.AddItem( new GUIContent( "Show 'Move' Activity" ), showMoveEntries, () => ToggleFilter( ref showMoveEntries ) );
			menu.AddItem( new GUIContent( "Show 'Rename' Activity" ), showRenameEntries, () => ToggleFilter( ref showRenameEntries ) );
			menu.AddItem( new GUIContent( "Show 'Restore' Activity" ), showRestoreEntries, () => ToggleFilter( ref showRestoreEntries ) );
		}

		private void ToggleFilter( ref bool filter )
		{
			filter = !filter;
			activityTreeView.SetFilters( showCreateEntries, showDeleteEntries, showEditEntries, showMoveEntries, showRenameEntries, showRestoreEntries );
		}

		private void OnEntriesRightClicked( ActivityEntry[] entries )
		{
			if( entries == null || entries.Length == 0 )
				return;

			GenericMenu contextMenu = new GenericMenu();

			contextMenu.AddItem( new GUIContent( "Download" ), false, () => new DownloadRequest() { fileIDs = entries.GetFileIDs() }.DownloadAsync() );

			contextMenu.AddSeparator( "" );

			contextMenu.AddItem( new GUIContent( "Show in Drive Browser" ), false, () =>
			{
				if( fileBrowser )
				{
					List<DriveFile> files = new List<DriveFile>( entries.Length );
					for( int i = 0; i < entries.Length; i++ )
					{
						if( !string.IsNullOrEmpty( entries[i].fileID ) )
							files.Add( DriveAPI.GetFileByID( entries[i].fileID ) );
					}

					fileBrowser.SetSelectionAsync( files );
					fileBrowser.Focus();
				}
			} );

			if( entries.Length == 1 && !string.IsNullOrEmpty( entries[0].fileID ) )
			{
				contextMenu.AddSeparator( "" );
				contextMenu.AddItem( new GUIContent( "View Activity" ), false, () => Initialize( fileBrowser, DriveAPI.GetFileByID( entries[0].fileID ) ) );
			}

			contextMenu.ShowAsContext();
			Repaint(); // Without this, context menu can appear seconds later which is annoying
		}

		private void OnGUI()
		{
			// Close any leftover windows after restarting Unity (this code somehow doesn't work inside OnEnable, condition is valid but Close function does nothing)
			if( inspectedFile == null || fileBrowser == null || fileBrowser.Equals( null ) )
			{
				Close();
				return;
			}

			if( shouldRepositionSelf )
			{
				shouldRepositionSelf = false;
				HelperFunctions.MoveWindowOverCursor( this, position.height );
			}

			Rect inspectedFileRect = EditorGUILayout.GetControlRect( true, EditorGUIUtility.singleLineHeight );
			GUI.Label( new Rect( inspectedFileRect.position, new Vector2( 80f, inspectedFileRect.height ) ), "Activity of:", EditorStyles.boldLabel );
			inspectedFileRect.xMin += 85f;
			GUI.Label( inspectedFileRect, new GUIContent
			{
				text = inspectedFile.name,
				image = inspectedFile.isFolder ? AssetDatabase.GetCachedIcon( "Assets" ) as Texture2D : UnityEditorInternal.InternalEditorUtility.GetIconForFile( inspectedFile.name )
			} );

			if( Event.current.type == EventType.MouseDown && inspectedFileRect.Contains( Event.current.mousePosition ) )
				fileBrowser.PingFile( inspectedFile );

			scrollPos = EditorGUILayout.BeginScrollView( scrollPos );

			activityTreeView.searchString = searchField.OnGUI( activityTreeView.searchString );
			activityTreeView.OnGUI( GUILayoutUtility.GetRect( 0, 100000, 0, 100000 ) );

			EditorGUILayout.EndScrollView();

			if( IsBusy )
			{
				EditorGUILayout.Space();

				GUI.enabled = cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested;
				if( GUILayout.Button( "Abort Operation..." ) )
					cancellationTokenSource.Cancel();
				GUI.enabled = true;

				EditorGUILayout.Space();
			}
			else if( !string.IsNullOrEmpty( pageToken ) )
			{
				EditorGUILayout.Space();

				if( GUILayout.Button( "Load More..." ) )
					LoadMoreFileActivityAsync();

				EditorGUILayout.Space();
			}

			// This happens only when the mouse click is not captured by the TreeView
			// In this case, clear the TreeView's selection
			if( Event.current.type == EventType.MouseDown && Event.current.button == 0 )
			{
				activityTreeView.SetSelection( new int[0] );
				EditorApplication.delayCall += Repaint;
			}
		}

		private async void LoadMoreFileActivityAsync()
		{
			if( IsBusy )
				return;

			IsBusy = true;
			try
			{
				cancellationTokenSource = new CancellationTokenSource();

				pageToken = await inspectedFile.GetActivityAsync( ( activityEntry ) =>
				{
					activity.Add( activityEntry );
					activityTreeView.Reload();

					Repaint();
				}, cancellationTokenSource.Token, MINIMUM_ENTRY_COUNT_PER_FETCH, pageToken );
			}
			finally
			{
				IsBusy = false;

				if( cancellationTokenSource != null )
				{
					cancellationTokenSource.Dispose();
					cancellationTokenSource = null;
				}
			}
		}
	}
}