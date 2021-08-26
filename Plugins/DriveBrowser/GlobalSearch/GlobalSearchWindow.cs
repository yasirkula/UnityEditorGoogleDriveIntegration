using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DriveBrowser
{
	public delegate void SearchResultEntryDelegate( DriveFile searchResultEntry );

	public class GlobalSearchWindow : EditorWindow
	{
		private const int MINIMUM_ENTRY_COUNT_PER_FETCH = 50;

		private FileBrowser fileBrowser;
		private string searchTerm;
		private string pageToken;

		private List<DriveFile> searchResults = new List<DriveFile>( 64 );

		private GlobalSearchTreeView searchResultsTreeView;
		private TreeViewState searchResultsTreeViewState;
		private MultiColumnHeaderState searchResultsTreeViewHeaderState;
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

		public static GlobalSearchWindow Initialize( FileBrowser fileBrowser, string searchTerm )
		{
			GlobalSearchWindow window = CreateInstance<GlobalSearchWindow>();
#if UNITY_2019_3_OR_NEWER
			window.titleContent = new GUIContent( "Search", EditorGUIUtility.IconContent( "Search Icon" ).image );
#else
			window.titleContent = new GUIContent( "Search" );
#endif
			window.minSize = new Vector2( 400f, 175f );

			window.fileBrowser = fileBrowser;
			window.searchTerm = searchTerm;

			window.Show();
			EditorApplication.delayCall += () => window.LoadMoreSearchResultsAsync();

			return window;
		}

		private void OnEnable()
		{
			if( searchResultsTreeViewState == null )
				searchResultsTreeViewState = new TreeViewState();

			MultiColumnHeader multiColumnHeader = HelperFunctions.GenerateMultiColumnHeader( ref searchResultsTreeViewHeaderState, 0,
				new HelperFunctions.HeaderColumn( "Name", true, true, 30f, 0f, 0f ),
				new HelperFunctions.HeaderColumn( "Size", false, false, 30f, 0f, 70f ),
				new HelperFunctions.HeaderColumn( "Last Modified", false, false, 30f, 0f, 100f ) );

			searchResultsTreeView = new GlobalSearchTreeView( searchResultsTreeViewState, multiColumnHeader, searchResults, OnFilesRightClicked );

			searchField = new SearchField();
			searchField.downOrUpArrowKeyPressed += searchResultsTreeView.SetFocusAndEnsureSelectedItem;
		}

		private void OnDisable()
		{
			if( cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested )
				cancellationTokenSource.Cancel();

			IsBusy = false;
		}

		private void OnFilesRightClicked( DriveFile[] files )
		{
			if( files == null || files.Length == 0 )
				return;

			GenericMenu contextMenu = new GenericMenu();

			contextMenu.AddItem( new GUIContent( "Download" ), false, () => new DownloadRequest() { fileIDs = files.GetFileIDs() }.DownloadAsync() );

			contextMenu.AddSeparator( "" );

			contextMenu.AddItem( new GUIContent( "Show in Drive Browser" ), false, () =>
			{
				if( fileBrowser )
				{
					fileBrowser.SetSelectionAsync( files );
					fileBrowser.Focus();
				}
			} );

			if( files.Length == 1 && !string.IsNullOrEmpty( files[0].id ) )
			{
				contextMenu.AddSeparator( "" );
				contextMenu.AddItem( new GUIContent( "View Activity" ), false, () => ActivityViewer.Initialize( fileBrowser, DriveAPI.GetFileByID( files[0].id ) ) );
			}

			contextMenu.ShowAsContext();
			Repaint(); // Without this, context menu can appear seconds later which is annoying
		}

		private void OnGUI()
		{
			// Close any leftover windows after restarting Unity (this code somehow doesn't work inside OnEnable, condition is valid but Close function does nothing)
			if( string.IsNullOrEmpty( searchTerm ) || fileBrowser == null || fileBrowser.Equals( null ) )
			{
				Close();
				return;
			}

			if( shouldRepositionSelf )
			{
				shouldRepositionSelf = false;
				HelperFunctions.MoveWindowOverCursor( this, position.height );
			}

			Rect searchTermRect = EditorGUILayout.GetControlRect( true, EditorGUIUtility.singleLineHeight );
			GUI.Label( new Rect( searchTermRect.position, new Vector2( 115f, searchTermRect.height ) ), "Search results for:", EditorStyles.boldLabel );
			searchTermRect.xMin += 120f;
			EditorGUI.TextField( searchTermRect, searchTerm );

			scrollPos = EditorGUILayout.BeginScrollView( scrollPos );

			searchResultsTreeView.searchString = searchField.OnGUI( searchResultsTreeView.searchString );
			searchResultsTreeView.OnGUI( GUILayoutUtility.GetRect( 0, 100000, 0, 100000 ) );

			EditorGUILayout.EndScrollView();

			if( IsBusy )
			{
				EditorGUILayout.Space();

				GUI.enabled = cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested;
				if( GUILayout.Button( "Abort Search..." ) )
					cancellationTokenSource.Cancel();
				GUI.enabled = true;

				EditorGUILayout.Space();
			}
			else if( !string.IsNullOrEmpty( pageToken ) )
			{
				EditorGUILayout.Space();

				if( GUILayout.Button( "Load More..." ) )
					LoadMoreSearchResultsAsync();

				EditorGUILayout.Space();
			}

			// This happens only when the mouse click is not captured by the TreeView
			// In this case, clear the TreeView's selection
			if( Event.current.type == EventType.MouseDown && Event.current.button == 0 )
			{
				searchResultsTreeView.SetSelection( new int[0] );
				EditorApplication.delayCall += Repaint;
			}
		}

		private async void LoadMoreSearchResultsAsync()
		{
			if( IsBusy )
				return;

			IsBusy = true;
			try
			{
				cancellationTokenSource = new CancellationTokenSource();

				pageToken = await DriveAPI.PerformGlobalSearchAsync( searchTerm, ( searchResultEntry ) =>
				{
					searchResults.Add( searchResultEntry );
					searchResultsTreeView.Reload();

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