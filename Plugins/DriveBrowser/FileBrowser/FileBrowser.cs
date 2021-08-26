using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DriveBrowser
{
	public class FileBrowser : EditorWindow, IHasCustomMenu, ISerializationCallbackReceiver
	{
		private FilesTreeView filesTreeView;
		private TreeViewState filesTreeViewState;
		private MultiColumnHeaderState filesTreeViewHeaderState;
		private SearchField searchField;

		private string[] fileIDToFileSerializedKeys;
		private DriveFile[] fileIDToFileSerializedValues;
		private string[] userIDToUsernameSerialized;

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
					{
#if UNITY_2019_1_OR_NEWER
						ShowNotification( new GUIContent( "Loading..." ), 10000.0 );
#else
						ShowNotification( new GUIContent( "Loading..." ) );
#endif
						HelperFunctions.LockAssemblyReload();
					}
					else
					{
						HelperFunctions.UnlockAssemblyReload();
						RemoveNotification();
					}

					Repaint();
				}
			}
		}

		[MenuItem( "Window/Drive Browser" )]
		public static void Initialize()
		{
			// Don't show this window until Google Cloud credentials are entered
			if( string.IsNullOrEmpty( GoogleCloudCredentials.Instance.ClientID ) || string.IsNullOrEmpty( GoogleCloudCredentials.Instance.ClientSecret ) )
				Selection.activeObject = GoogleCloudCredentials.Instance;
			else
			{
				FileBrowser window = GetWindow<FileBrowser>();
				window.titleContent = new GUIContent( "Drive Browser" );
				window.minSize = new Vector2( 300f, 150f );
				window.Show();
			}
		}

		private void Awake()
		{
			// Wait for OnEnable to initialize the TreeView
			EditorApplication.delayCall += () => RefreshFolderAsync( DriveAPI.RootFolder );
		}

		private void OnEnable()
		{
			if( filesTreeViewState == null )
				filesTreeViewState = new TreeViewState();

			MultiColumnHeader multiColumnHeader = HelperFunctions.GenerateMultiColumnHeader( ref filesTreeViewHeaderState, 0,
				new HelperFunctions.HeaderColumn( "Name", true, true, 30f, 0f, 0f ),
				new HelperFunctions.HeaderColumn( "Size", false, false, 30f, 0f, 70f ),
				new HelperFunctions.HeaderColumn( "Last Modified", false, false, 30f, 0f, 100f ) );

			filesTreeView = new FilesTreeView( filesTreeViewState, multiColumnHeader, DriveAPI.RootFolder, RefreshFolderAsync, OnFilesRightClicked );

			searchField = new SearchField();
			searchField.downOrUpArrowKeyPressed += filesTreeView.SetFocusAndEnsureSelectedItem;
		}

		private void OnDisable()
		{
			FilePreviewPopup.Dispose();
		}

		private void OnDestroy()
		{
			// Close all ActivityViewer windows with this window since they are tied to this window
			ActivityViewer[] activityViewerWindows = Resources.FindObjectsOfTypeAll<ActivityViewer>();
			if( activityViewerWindows != null )
			{
				foreach( ActivityViewer activityViewerWindow in activityViewerWindows )
				{
					if( activityViewerWindow != null && !activityViewerWindow.Equals( null ) )
						activityViewerWindow.Close();
				}
			}
		}

		void IHasCustomMenu.AddItemsToMenu( GenericMenu menu )
		{
			menu.AddItem( new GUIContent( "Help" ), false, () =>
			{
				EditorUtility.DisplayDialog( "Help",
					"- To download files, either drag them to the Project window or right click them and select 'Download'\n" +
					"- Downloads for files larger than 5 MB can sometimes take ~20 seconds to initialize, unfortunately\n" +
					"- Green folders aren't explored yet and when they are expanded, their contents will asynchronously be fetched from the Drive servers\n" +
					"- Green folders aren't included in search since their contents aren't known until they are expanded\n" +
					"- To refresh a folder's contents, right click the folder and select 'Refresh'\n" +
					"- To switch Google accounts, click the 'Reauthenticate' button\n" +
					"- Google documents aren't actual files and thus, their file sizes will be displayed as 0 bytes\n" +
					"- In File Activity window, files highlighted in red color are permanently deleted and can't be downloaded\n" +
					"- Asynchronous operations usually prevent code compilation until the operation is completed but if you feel like your code won't compile or " +
					"Unity won't enter Play mode although all asynchronous operations are completed, click the 'Unstuck Compilation Pipeline' button", "OK" );
			} );

			menu.AddSeparator( "" );

			menu.AddItem( new GUIContent( "Refresh Root" ), false, () => RefreshFolderAsync( DriveAPI.RootFolder ) );

			menu.AddSeparator( "" );

			menu.AddItem( new GUIContent( "Reauthenticate" ), false, () =>
			{
				DriveAPI.RevokeAuthentication();
				RefreshFolderAsync( DriveAPI.RootFolder );
			} );

			menu.AddSeparator( "" );

			menu.AddItem( new GUIContent( "Clear Preview Cache" ), false, () => DriveAPI.ClearThumbnailCache() );

			menu.AddItem( new GUIContent( "Unstuck Compilation Pipeline" ), false, () => HelperFunctions.UnlockAssemblyReload() );
		}

		// Serialize DriveAPI's Dictionaries in this EditorWindow's arrays
		void ISerializationCallbackReceiver.OnBeforeSerialize()
		{
			DriveAPI.Serialize( out userIDToUsernameSerialized, out fileIDToFileSerializedKeys, out fileIDToFileSerializedValues );
		}

		void ISerializationCallbackReceiver.OnAfterDeserialize()
		{
			DriveAPI.Deserialize( userIDToUsernameSerialized, fileIDToFileSerializedKeys, fileIDToFileSerializedValues );
		}

		private void OnFilesRightClicked( DriveFile[] files )
		{
			if( IsBusy || files == null || files.Length == 0 )
				return;

			GenericMenu contextMenu = new GenericMenu();

			if( files.Length == 1 )
			{
				if( files[0].isFolder )
				{
					contextMenu.AddItem( new GUIContent( "Refresh" ), false, () => RefreshFolderAsync( files[0] ) );
					contextMenu.AddSeparator( "" );
				}

				contextMenu.AddItem( new GUIContent( "Download" ), false, () => new DownloadRequest() { fileIDs = new string[1] { files[0].id } }.DownloadAsync() );
				contextMenu.AddSeparator( "" );
				contextMenu.AddItem( new GUIContent( "Open in Browser" ), false, () => files[0].OpenInBrowserAsync() );

				if( files[0].size > 0L )
				{
					contextMenu.AddSeparator( "" );
					contextMenu.AddItem( new GUIContent( "MD5/Print" ), false, async () => Debug.Log( await files[0].GetMD5HashAsync() ) );
					contextMenu.AddItem( new GUIContent( "MD5/Compare" ), false, async () =>
					{
						string comparedFilePath = EditorUtility.OpenFilePanel( "Compare MD5 hash with file", Application.dataPath, "" );
						if( string.IsNullOrEmpty( comparedFilePath ) )
							return;

						string driveFileHash = await files[0].GetMD5HashAsync();
						string localFileHash = HelperFunctions.CalculateMD5Hash( comparedFilePath );
						Debug.Log( string.Concat( ( driveFileHash == localFileHash ) ? "<b>MD5 hashes match:</b>\n" : "<b>MD5 hashes don't match:</b>\n",
							"(Drive) ", files[0].name, ": <b>", driveFileHash, "</b>\n",
							"(Local) ", comparedFilePath, ": <b>", localFileHash, "</b>" ) );
					} );
				}

				contextMenu.AddSeparator( "" );
				contextMenu.AddItem( new GUIContent( "View Activity" ), false, () => ActivityViewer.Initialize( this, files[0] ) );
			}
			else
				contextMenu.AddItem( new GUIContent( "Download" ), false, () => new DownloadRequest() { fileIDs = files.GetFileIDs() }.DownloadAsync() );

			contextMenu.ShowAsContext();
			Repaint(); // Without this, context menu can appear seconds later which is annoying
		}

		private void OnGUI()
		{
			// Remove preview popup on mouse scroll wheel events
			if( Event.current.type == EventType.ScrollWheel )
				FilePreviewPopup.Hide();

			GUI.enabled = !IsBusy;

			scrollPos = EditorGUILayout.BeginScrollView( scrollPos );

			EditorGUI.BeginChangeCheck();
			bool wasSearching = !string.IsNullOrEmpty( filesTreeView.searchString );
			filesTreeView.searchString = searchField.OnGUI( filesTreeView.searchString );
			if( EditorGUI.EndChangeCheck() && filesTreeView.HasSelection() )
			{
				bool isSearching = !string.IsNullOrEmpty( filesTreeView.searchString );
				if( isSearching && !wasSearching ) // Clear selection when entering search because otherwise it can cause false-positives with the next 'else if' condition
					filesTreeView.SetSelection( new int[0] );
				else if( !isSearching && wasSearching ) // Focus on the selected item when exiting search so that we can see the file's location
					filesTreeView.SetSelection( filesTreeView.GetSelection(), TreeViewSelectionOptions.RevealAndFrame );
			}

			filesTreeView.OnGUI( GUILayoutUtility.GetRect( 0, 100000, 0, 100000 ) );

			// This happens only when the mouse click is not captured by the TreeView
			// In this case, clear the TreeView's selection
			if( Event.current.type == EventType.MouseDown && Event.current.button == 0 )
			{
				filesTreeView.SetSelection( new int[0] );
				EditorApplication.delayCall += Repaint;
			}

			EditorGUILayout.EndScrollView();

			GUI.enabled = true;
		}

		private async void RefreshFolderAsync( DriveFile folder )
		{
			if( IsBusy )
				return;

			IsBusy = true;
			try
			{
				await folder.RefreshContentsAsync();

				filesTreeView.searchString = "";
				filesTreeView.Refresh( folder );
			}
			finally
			{
				IsBusy = false;
			}
		}

		public void PingFile( DriveFile file )
		{
			filesTreeView.SetSelection( new int[1] { file.id.GetHashCode() }, TreeViewSelectionOptions.RevealAndFrame );
			Repaint();
		}

		public async void SetSelectionAsync( IList<DriveFile> files )
		{
			if( IsBusy || files == null || files.Count == 0 )
				return;

			IsBusy = true;
			try
			{
				HashSet<string> exploredFolders = new HashSet<string>();
				List<int> exploredFolderIDs = new List<int>( files.Count * 4 );
				List<int> fileIDs = new List<int>( files.Count );

				filesTreeView.searchString = "";

				await DriveAPI.RootFolder.RefreshContentsAsync();

				foreach( DriveFile file in files )
				{
					if( file == null || string.IsNullOrEmpty( file.id ) )
						continue;

					// Explore the contents of all directories leading to this file so that they can be expanded in FilesTreeView
					DriveFile[] fileHierarchy = await file.LoadFileHierarchyAsync();
					for( int i = 0; i < fileHierarchy.Length - 1; i++ )
					{
						if( !exploredFolders.Contains( fileHierarchy[i].id ) )
						{
							exploredFolders.Add( fileHierarchy[i].id );
							exploredFolderIDs.Add( fileHierarchy[i].id.GetHashCode() );

							await fileHierarchy[i].RefreshContentsAsync();
						}
					}

					if( !fileIDs.Contains( file.id.GetHashCode() ) )
						fileIDs.Add( file.id.GetHashCode() );
				}

				filesTreeView.SetExpanded( exploredFolderIDs );

				if( fileIDs.Count > 0 )
				{
					filesTreeView.Reload();
					filesTreeView.SetSelection( fileIDs, TreeViewSelectionOptions.RevealAndFrame );

					Repaint();
				}
			}
			finally
			{
				IsBusy = false;
			}
		}
	}
}