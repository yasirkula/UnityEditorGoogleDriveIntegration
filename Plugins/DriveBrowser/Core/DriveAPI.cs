using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.DriveActivity.v2;
using Google.Apis.DriveActivity.v2.Data;
using Google.Apis.PeopleService.v1;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using File = System.IO.File;
using DFile = Google.Apis.Drive.v3.Data.File;
using PPerson = Google.Apis.PeopleService.v1.Data.Person;

namespace DriveBrowser
{
	public static class DriveAPI
	{
		private enum DownloadConflictResolution { Undetermined = 0, AlwaysAsk = 1, AlwaysOverwrite = 2, AlwaysUseUniqueName = 3, AlwaysSkip = 4 };

		private const string AUTH_TOKEN_PATH = "Library/DriveTokens";
		private const string THUMBNAILS_DOWNLOAD_PATH = "Library/DriveThumbnails";
		private const string REQUIRED_FILE_FIELDS = "id, name, mimeType, size, modifiedTime, parents";
		private const string ROOT_FOLDER_ID = "Hello world, my old friend";

		private const int MAX_CONCURRENT_DOWNLOAD_COUNT = 3;
		private const int DOWNLOAD_PROGRESS_REPORT_INTERVAL = 1_048_576; // Progress changes will be reported every 1 MB

		private static DriveService driveAPI;
		private static DriveActivityService driveActivityAPI;
		private static PeopleServiceService peopleAPI;

		private static readonly Dictionary<string, DriveFile> fileIDToFile = new Dictionary<string, DriveFile>( 1024 );
		private static readonly Dictionary<string, string> userIDToUsername = new Dictionary<string, string>( 64 );

		public static DriveFile RootFolder
		{
			get
			{
				if( !fileIDToFile.TryGetValue( ROOT_FOLDER_ID, out DriveFile rootFolder ) )
				{
					rootFolder = new DriveFile( new DFile() )
					{
						id = ROOT_FOLDER_ID,
						isFolder = true
					};

					fileIDToFile[ROOT_FOLDER_ID] = rootFolder;
				}

				return rootFolder;
			}
		}

		private static readonly List<DriveFile> folderContents = new List<DriveFile>( 64 );

		private static DownloadConflictResolution downloadConflictResolution;

		public static void Serialize( out string[] userIDToUsernameSerialized, out string[] fileIDToFileSerializedKeys, out DriveFile[] fileIDToFileSerializedValues )
		{
			userIDToUsername.SerializeToArray( out userIDToUsernameSerialized );
			fileIDToFile.SerializeToArray( out fileIDToFileSerializedKeys, out fileIDToFileSerializedValues );
		}

		public static void Deserialize( string[] userIDToUsernameSerialized, string[] fileIDToFileSerializedKeys, DriveFile[] fileIDToFileSerializedValues )
		{
			userIDToUsername.DeserializeFromArray( userIDToUsernameSerialized );
			fileIDToFile.DeserializeFromArray( fileIDToFileSerializedKeys, fileIDToFileSerializedValues );
		}

		public static async Task RefreshContentsAsync( this DriveFile folder )
		{
			// We may call this function from anywhere using a cached DriveFile. During serialization or a prior RefreshContentsAsync call,
			// the fileIdToFile[folder.id] may no longer point to the DriveFile we have but rather an updated version of it. We always want to
			// refresh the contents of the up-to-date folder because we are modifying its children and childrenState; and modifying these variables
			// for a DriveFile that is no longer a part of the file hierarchy wouldn't make sense
			folder = fileIDToFile[folder.id];

			bool isRootFolder = folder == RootFolder;
			folderContents.Clear();

			try
			{
				string pageToken = null;
				do
				{
					FilesResource.ListRequest request = ( await GetDriveAPIAsync() ).Files.List();
					request.PageSize = 50;
					request.Fields = $"nextPageToken, files({REQUIRED_FILE_FIELDS})";
					request.PageToken = pageToken;

					if( isRootFolder )
						request.Q = "('root' in parents or sharedWithMe = true) and trashed = false and mimeType != 'application/vnd.google-apps.shortcut'";
					else
						request.Q = "'" + folder.id + "' in parents and trashed = false";

					FileList result = await request.ExecuteAsync();
					if( result.Files != null )
					{
						foreach( DFile file in result.Files )
						{
							// Root files should have their parentID set to null
							file.Parents = isRootFolder ? null : new string[1] { folder.id };

							DriveFile _file = new DriveFile( file );
							folderContents.Add( _file );

							// Try not to lose the whole cached hierarchy while refreshing a root folder
							if( fileIDToFile.TryGetValue( file.Id, out DriveFile previouslyCachedFile ) )
							{
								_file.children = previouslyCachedFile.children;
								_file.childrenState = previouslyCachedFile.childrenState;
							}

							fileIDToFile[file.Id] = _file;
						}
					}

					pageToken = result.NextPageToken;
				} while( pageToken != null );

				folder.childrenState = ( folderContents.Count > 0 ) ? FolderChildrenState.HasChildren : FolderChildrenState.NoChildren;
				folder.children = new string[folderContents.Count];
				for( int i = 0; i < folderContents.Count; i++ )
					folder.children[i] = folderContents[i].id;
			}
			catch( System.Exception e )
			{
				Debug.LogException( e );
				folderContents.Clear();
			}
		}

		public static async Task<string> GetActivityAsync( this DriveFile file, ActivityEntryDelegate onEntryReceived, int minimumEntryCount = 20, string pageToken = null )
		{
			List<string> fetchedActivity = new List<string>( minimumEntryCount * 2 );
			try
			{
				int receivedEntryCount = 0;
				do
				{
					QueryDriveActivityRequest request = new QueryDriveActivityRequest()
					{
						PageSize = minimumEntryCount,
						Filter = "detail.action_detail_case:(CREATE EDIT RENAME MOVE DELETE RESTORE)",
						PageToken = string.IsNullOrEmpty( pageToken ) ? null : pageToken
					};

					if( file.isFolder )
						request.AncestorName = "items/" + file.id;
					else
						request.ItemName = "items/" + file.id;

					QueryDriveActivityResponse result = await ( await GetDriveActivityAPIAsync() ).Activity.Query( request ).ExecuteAsync();
					if( result.Activities != null )
					{
						StringBuilder sb = new StringBuilder( 200 );

						foreach( DriveActivity activity in result.Activities )
						{
							if( activity.Targets == null )
								continue;

							foreach( Target targetFile in activity.Targets )
							{
								if( targetFile.DriveItem == null )
									continue;

								ActivityEntry activityEntry = new ActivityEntry();

								if( activity.Timestamp != null && activity.Timestamp is System.DateTime )
									activityEntry.timeTicks = ( (System.DateTime) activity.Timestamp ).Ticks;
								else if( activity.TimeRange != null && activity.TimeRange.EndTime is System.DateTime )
									activityEntry.timeTicks = ( (System.DateTime) activity.TimeRange.EndTime ).Ticks;

								activityEntry.username = ( activity.Actors != null && activity.Actors.Count > 0 ) ? await activity.Actors[0].GetUsernameAsync() : "Unknown User";

								if( activity.PrimaryActionDetail.Create != null )
									activityEntry.type = FileActivityType.Create;
								else if( activity.PrimaryActionDetail.Edit != null )
									activityEntry.type = FileActivityType.Edit;
								else if( activity.PrimaryActionDetail.Rename != null )
									activityEntry.type = FileActivityType.Rename;
								else if( activity.PrimaryActionDetail.Move != null )
									activityEntry.type = FileActivityType.Move;
								else if( activity.PrimaryActionDetail.Delete != null )
									activityEntry.type = FileActivityType.Delete;
								else if( activity.PrimaryActionDetail.Restore != null )
									activityEntry.type = FileActivityType.Restore;
								else
									continue; // We aren't interested in other event types

								DriveFile changedFile = await GetFileByIDAsync( targetFile.DriveItem.Name.Replace( "items/", "" ) );
								if( changedFile == null )
								{
									activityEntry.isFolder = ( targetFile.DriveItem.DriveFolder != null );
									activityEntry.size = -1L;

									// Alternative method of calculating relativePath that doesn't require DriveFile; but it can't find more than one parent directories
									sb.Length = 0;

									if( activity.Actions != null )
									{
										foreach( Action activityDetails in activity.Actions )
										{
											if( activityDetails.Detail != null && activityDetails.Detail.Move != null && activityDetails.Detail.Move.AddedParents != null && activityDetails.Detail.Move.AddedParents.Count > 0 && activityDetails.Detail.Move.AddedParents[0].DriveItem != null )
											{
												sb.Append( activityDetails.Detail.Move.AddedParents[0].DriveItem.Title ).Append( "/" );
												break;
											}
										}
									}

									activityEntry.relativePath = sb.Append( targetFile.DriveItem.Title ).ToString();
								}
								else
								{
									activityEntry.fileID = changedFile.id;
									activityEntry.isFolder = changedFile.isFolder;
									activityEntry.size = changedFile.size;

									DriveFile[] fileHierarchy = await changedFile.LoadFileHierarchyAsync( file.id );
									if( fileHierarchy.Length == 1 )
										activityEntry.relativePath = targetFile.DriveItem.Title;
									else
									{
										sb.Length = 0;
										for( int i = 0; i < fileHierarchy.Length - 1; i++ )
											sb.Append( fileHierarchy[i].name ).Append( "/" );

										activityEntry.relativePath = sb.Append( targetFile.DriveItem.Title ).ToString();
									}
								}

								onEntryReceived?.Invoke( activityEntry );
								receivedEntryCount++;
							}
						}
					}

					pageToken = result.NextPageToken;
				} while( pageToken != null && receivedEntryCount < minimumEntryCount );
			}
			catch( System.Exception e )
			{
				Debug.LogException( e );
			}

			return pageToken;
		}

		public static async Task<string> GetUsernameAsync( this Actor actor )
		{
			if( actor == null || actor.User == null || actor.User.KnownUser == null )
				return "Unknown User";

			if( userIDToUsername.TryGetValue( actor.User.KnownUser.PersonName, out string cachedResult ) )
				return cachedResult;

			string username = "Unknown User";
			try
			{
				PeopleResource.GetRequest request = ( await GetPeopleAPIAsync() ).People.Get( actor.User.KnownUser.PersonName );
				request.PersonFields = "names";

				PPerson result = await request.ExecuteAsync();
				if( result.Names != null && result.Names.Count > 0 )
					username = result.Names[0].DisplayName;

				userIDToUsername[actor.User.KnownUser.PersonName] = username;
			}
			catch( System.Exception e )
			{
				Debug.LogException( e );
			}

			return username;
		}

		public static async void DownloadAsync( this DownloadRequest downloadRequest )
		{
			// First, filter the files so that there are no duplicates or no parent-child relationships (which would result in duplicate downloads)
			List<DriveFile> filesToDownload = new List<DriveFile>();
			for( int i = 0; i < downloadRequest.fileIDs.Length; i++ )
			{
				if( string.IsNullOrEmpty( downloadRequest.fileIDs[i] ) )
					continue;

				DriveFile file = fileIDToFile[downloadRequest.fileIDs[i]];
				if( filesToDownload.Contains( file ) )
					continue;

				// 1. If this file is parent of other files in the list, remove those child files from the list
				// 2. If another file in the list is parent of this file, don't add this file to the list
				bool shouldDownloadFile = true;
				for( int j = filesToDownload.Count - 1; j >= 0; j-- )
				{
					if( file.IsAncestorOf( filesToDownload[j] ) )
						filesToDownload.RemoveAt( j );
					else if( filesToDownload[j].IsAncestorOf( file ) )
					{
						shouldDownloadFile = false;
						break;
					}
				}

				if( shouldDownloadFile )
					filesToDownload.Add( file );
			}

			if( filesToDownload.Count == 0 )
			{
				Debug.LogWarning( "No files to download..." );
				return;
			}

			// Pick the download folder if it isn't already determined
			if( string.IsNullOrEmpty( downloadRequest.path ) )
			{
				downloadRequest.path = EditorUtility.OpenFolderPanel( "Download file(s) to", "Assets", "" );
				if( string.IsNullOrEmpty( downloadRequest.path ) )
					return;
			}

			await GetDriveAPIAsync();

			// If we are downloading a single file, don't show the "Always Overwrite" dialog. Otherwise, show it after the first conflict
			downloadConflictResolution = ( filesToDownload.Count > 1 || filesToDownload[0].isFolder ) ? DownloadConflictResolution.Undetermined : DownloadConflictResolution.AlwaysAsk;

			CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
			DownloadProgressViewer downloadProgressViewer = DownloadProgressViewer.Initialize( filesToDownload.Count, cancellationTokenSource );

			HelperFunctions.LockAssemblyReload();
			try
			{
				CancellationToken cancellationToken = cancellationTokenSource.Token;

				using( SemaphoreSlim downloadThrottler = new SemaphoreSlim( 0, MAX_CONCURRENT_DOWNLOAD_COUNT ) )
				{
					Task[] downloadTasks = new Task[filesToDownload.Count];
					for( int i = 0; i < filesToDownload.Count; i++ )
						downloadTasks[i] = filesToDownload[i].DownloadAsync( downloadRequest.path, downloadProgressViewer, downloadThrottler, cancellationToken );

					downloadThrottler.Release( MAX_CONCURRENT_DOWNLOAD_COUNT );

					await Task.WhenAll( downloadTasks );
				}

				if( cancellationToken.IsCancellationRequested )
					Debug.Log( "Download canceled" );
			}
			catch( System.Exception e )
			{
				Debug.LogError( "FATAL DOWNLOAD ERROR" );
				Debug.LogException( e );
			}
			finally
			{
				HelperFunctions.UnlockAssemblyReload();

				cancellationTokenSource.Dispose();
				downloadProgressViewer.DownloadCompleted();

				AssetDatabase.Refresh();
			}
		}

		private static async Task DownloadAsync( this DriveFile file, string directory, DownloadProgressViewer downloadProgressViewer, SemaphoreSlim downloadThrottler, CancellationToken cancellationToken )
		{
			bool throttledDownload = false;
			try
			{
				if( downloadThrottler != null )
				{
					throttledDownload = true;
					await downloadThrottler.WaitAsync( cancellationToken );
				}

				if( cancellationToken.IsCancellationRequested )
					return;

				// Credit: https://stackoverflow.com/a/23182807/2373034
				string validFilename = string.Concat( file.name.Split( Path.GetInvalidFileNameChars() ) );

				downloadProgressViewer.AddDownload( file );

				if( file.isFolder )
				{
					if( file.childrenState == FolderChildrenState.Unknown )
						await file.RefreshContentsAsync();

					if( throttledDownload )
					{
						throttledDownload = false;
						downloadThrottler.Release();
					}

					downloadProgressViewer.RemoveDownload( file );

					string downloadPath = ProcessFileDownloadPath( Path.Combine( directory, validFilename ), true, out bool shouldSkipFolder );
					if( shouldSkipFolder )
						return;

					downloadProgressViewer.IncrementTotalFileCount( file.children.Length );

					Directory.CreateDirectory( downloadPath );

					Task[] downloadTasks = new Task[file.children.Length];
					for( int i = 0; i < file.children.Length; i++ )
						downloadTasks[i] = fileIDToFile[file.children[i]].DownloadAsync( downloadPath, downloadProgressViewer, downloadThrottler, cancellationToken );

					await Task.WhenAll( downloadTasks );
				}
				else
				{
					FilesResource.GetRequest request = driveAPI.Files.Get( file.id );
					request.Fields = "copyRequiresWriterPermission, exportLinks";

					DFile _file = await request.ExecuteAsync( cancellationToken );
					if( _file.CopyRequiresWriterPermission.Value )
					{
						Debug.LogWarning( "Can't download '" + file.name + "' because the owner has restricted access to it" );
						return;
					}

					if( _file.ExportLinks != null && _file.ExportLinks.Count > 0 )
					{
						// This is a Drive document, we need to export it
						string mimeType = "application/pdf", exportUrl;
						if( !_file.ExportLinks.TryGetValue( mimeType, out exportUrl ) )
						{
							// If PDF export isn't supported, fallback to a supported mime type
							foreach( KeyValuePair<string, string> kvPair in _file.ExportLinks )
							{
								mimeType = kvPair.Key;
								exportUrl = kvPair.Value;

								break;
							}
						}

						Debug.Log( "Exporting '" + file.name + "' document with mime: " + mimeType );

						// Determine file path
						const string EXPORT_FORMAT_TEXT = "&exportFormat=";
						int exportExtensionIndex = exportUrl.IndexOf( EXPORT_FORMAT_TEXT );
						string exportExtension = ( exportExtensionIndex >= 0 ) ? ( "." + exportUrl.Substring( exportExtensionIndex + EXPORT_FORMAT_TEXT.Length ) ) : "";

						string downloadPath = ProcessFileDownloadPath( Path.Combine( directory, validFilename + exportExtension ), false, out bool shouldSkipFile );
						if( shouldSkipFile )
							return;

						try
						{
							using( FileStream fileStream = File.Create( downloadPath ) )
							{
								FilesResource.ExportRequest exportRequest = driveAPI.Files.Export( file.id, mimeType );
								exportRequest.MediaDownloader.ProgressChanged += ( progress ) => downloadProgressViewer.SetProgress( file, progress.BytesDownloaded );
								exportRequest.MediaDownloader.ChunkSize = DOWNLOAD_PROGRESS_REPORT_INTERVAL;
								Google.Apis.Download.IDownloadProgress exportResult = await exportRequest.DownloadAsync( fileStream, cancellationToken );
								if( exportResult.Status == Google.Apis.Download.DownloadStatus.Failed )
									Debug.LogWarning( "Failed to export: " + file.name + " " + exportResult.Exception );
							}
						}
						catch( System.OperationCanceledException ) { }

						if( cancellationToken.IsCancellationRequested )
						{
							File.Delete( downloadPath );
							File.Delete( downloadPath + ".meta" );
						}
					}
					else
					{
						// This is a normal file, we need to download it
						string downloadPath = ProcessFileDownloadPath( Path.Combine( directory, validFilename ), false, out bool shouldSkipFile );
						if( shouldSkipFile )
							return;

						try
						{
							using( FileStream fileStream = File.Create( downloadPath ) )
							{
								FilesResource.GetRequest downloadRequest = driveAPI.Files.Get( file.id );
								downloadRequest.MediaDownloader.ProgressChanged += ( progress ) => downloadProgressViewer.SetProgress( file, progress.BytesDownloaded );
								downloadRequest.MediaDownloader.ChunkSize = DOWNLOAD_PROGRESS_REPORT_INTERVAL;
								Google.Apis.Download.IDownloadProgress downloadResult = await downloadRequest.DownloadAsync( fileStream, cancellationToken );
								if( downloadResult.Status == Google.Apis.Download.DownloadStatus.Failed )
								{
									// Drive downloads can fail for large downloads with error message 'cannotDownloadAbusiveFile'
									Google.GoogleApiException apiException = downloadResult.Exception as Google.GoogleApiException;
									if( apiException == null || !apiException.CheckErrorType( "cannotDownloadAbusiveFile" ) )
										Debug.LogWarning( "Failed to download: " + file.name + " " + downloadResult.Exception );
									else
									{
										//Debug.Log( "DEBUG: Downloading large file..." );

										// Setting AcknowledgeAbuse to true gets rid of the 'cannotDownloadAbusiveFile' error
										downloadRequest.AcknowledgeAbuse = true;
										downloadResult = await downloadRequest.DownloadAsync( fileStream, cancellationToken );
										if( downloadResult.Status == Google.Apis.Download.DownloadStatus.Failed )
											Debug.LogWarning( "Failed to download: " + file.name + " " + downloadResult.Exception );
									}
								}
							}
						}
						catch( System.OperationCanceledException ) { }

						if( cancellationToken.IsCancellationRequested )
						{
							File.Delete( downloadPath );
							File.Delete( downloadPath + ".meta" );
						}
					}
				}
			}
			catch( Google.GoogleApiException e )
			{
				if( e.CheckErrorType( "notFound" ) )
				{
					Debug.LogWarning( "Can't download '" + file.name + "' because it seems like the file no longer exists" );

					// Remove the deleted file from cached file hierarchy
					DriveFile parentFolder = string.IsNullOrEmpty( file.parentID ) ? RootFolder : fileIDToFile[file.parentID];
					List<string> parentFolderChildren = new List<string>( parentFolder.children );
					parentFolderChildren.Remove( file.id );
					parentFolder.children = parentFolderChildren.ToArray();
				}
				else if( e.CheckErrorType( "exportSizeLimitExceeded" ) )
					Debug.LogWarning( "Can't export '" + file.name + "' because its file size exceeds the 10 MB limit" );
				else
					Debug.LogException( e );
			}
			catch( System.OperationCanceledException ) { }
			catch( System.Exception e )
			{
				Debug.LogException( e );
			}
			finally
			{
				if( throttledDownload && !cancellationToken.IsCancellationRequested )
					downloadThrottler.Release();

				downloadProgressViewer.RemoveDownload( file );
			}
		}

		private static string ProcessFileDownloadPath( string downloadPath, bool isDirectory, out bool shouldSkipFile )
		{
			shouldSkipFile = false;

			if( isDirectory ? Directory.Exists( downloadPath ) : File.Exists( downloadPath ) )
			{
				int conflictResolutionStrategy;
				switch( downloadConflictResolution )
				{
					case DownloadConflictResolution.AlwaysOverwrite: conflictResolutionStrategy = 0; break;
					case DownloadConflictResolution.AlwaysSkip: conflictResolutionStrategy = 1; break;
					case DownloadConflictResolution.AlwaysUseUniqueName: conflictResolutionStrategy = 2; break;
					default:
					{
						string noun = isDirectory ? "Folder" : "File";
						conflictResolutionStrategy = EditorUtility.DisplayDialogComplex( $"{noun} Conflict", $"{noun} '" + Path.GetFileName( downloadPath ) + "' already exists at path: " + Path.GetDirectoryName( downloadPath ), isDirectory ? "Append" : "Overwrite", "Skip", "Use unique name" );

						break;
					}
				}

				switch( conflictResolutionStrategy )
				{
					case 0:
					{
						// Overwrite/Append
						if( downloadConflictResolution == DownloadConflictResolution.Undetermined )
							downloadConflictResolution = EditorUtility.DisplayDialog( "Always Append/Overwrite", "When another conflict occurs, should the conflict automatically be resolved with Append (for directories) and Overwrite (for files)?", "Always Append/Overwrite", "Always Ask" ) ? DownloadConflictResolution.AlwaysOverwrite : DownloadConflictResolution.AlwaysAsk;

						break;
					}
					case 1:
					{
						// Skip
						shouldSkipFile = true;

						if( downloadConflictResolution == DownloadConflictResolution.Undetermined )
							downloadConflictResolution = EditorUtility.DisplayDialog( "Always Skip", "When another conflict occurs, should the conflicted file/folder automatically be skipped?", "Always Skip", "Always Ask" ) ? DownloadConflictResolution.AlwaysSkip : DownloadConflictResolution.AlwaysAsk;

						break;
					}
					case 2:
					{
						// Use unique name
						string extension = isDirectory ? "" : Path.GetExtension( downloadPath );
						if( extension == null )
							extension = "";

						string downloadPathWithoutExtension = downloadPath.Substring( 0, downloadPath.Length - extension.Length ) + " ";
						int fileSuffix = 1;
						do
						{
							downloadPath = downloadPathWithoutExtension + ( fileSuffix++ ) + extension;
						} while( isDirectory ? Directory.Exists( downloadPath ) : File.Exists( downloadPath ) );

						if( downloadConflictResolution == DownloadConflictResolution.Undetermined )
							downloadConflictResolution = EditorUtility.DisplayDialog( "Always Use Unique Name", "When another conflict occurs, should the conflict automatically be resolved by using a unique name?", "Always Use Unique Name", "Always Ask" ) ? DownloadConflictResolution.AlwaysUseUniqueName : DownloadConflictResolution.AlwaysAsk;

						break;
					}
				}
			}

			return downloadPath;
		}

		public static async Task<string> GetThumbnailAsync( this DriveFile file, CancellationToken cancellationToken )
		{
			try
			{
				string thumbnailPath = THUMBNAILS_DOWNLOAD_PATH + "/" + file.id;
				FileInfo thumbnailFile = new FileInfo( thumbnailPath );
				if( thumbnailFile.Exists )
					return thumbnailFile.Length > 0L ? thumbnailPath : null;

				Directory.CreateDirectory( THUMBNAILS_DOWNLOAD_PATH );

				FilesResource.GetRequest request = ( await GetDriveAPIAsync() ).Files.Get( file.id );
				request.Fields = "thumbnailLink";

				DFile _file = await request.ExecuteAsync( cancellationToken );
				string thumbnailLink = _file.ThumbnailLink;
				if( string.IsNullOrEmpty( thumbnailLink ) )
				{
					thumbnailFile.Create().Close(); // Create an empty thumbnail file in cache
					return null;
				}

				if( cancellationToken.IsCancellationRequested )
					return null;

				using( Stream networkStream = await driveAPI.HttpClient.GetStreamAsync( thumbnailLink ) )
				using( FileStream fileStream = thumbnailFile.Create() )
				{
					// Not passing CancellationToken to CopyToAsync because once the cached file is created, we don't want to leave
					// its contents empty by canceling the copy operation. Since thumbnail files are very small (~10KB on average),
					// this copy operation should take negligible time anyways
					await networkStream.CopyToAsync( fileStream, 81920 );
				}

				return thumbnailPath;
			}
			catch( Google.GoogleApiException e )
			{
				if( !e.CheckErrorType( "notFound" ) )
					Debug.LogException( e );

				return null;
			}
			catch( System.OperationCanceledException )
			{
				return null;
			}
			catch( System.Exception e )
			{
				Debug.LogException( e );
				return null;
			}
		}

		public static async Task<string> GetMD5HashAsync( this DriveFile file )
		{
			FilesResource.GetRequest request = ( await GetDriveAPIAsync() ).Files.Get( file.id );
			request.Fields = "md5Checksum";

			return ( await request.ExecuteAsync() ).Md5Checksum;
		}

		public static async void OpenInBrowserAsync( this DriveFile file )
		{
			FilesResource.GetRequest request = ( await GetDriveAPIAsync() ).Files.Get( file.id );
			request.Fields = "webViewLink";

			string url = ( await request.ExecuteAsync() ).WebViewLink;
			Application.OpenURL( url );
		}

		// The DriveFile must be loaded before or it will throw Exception!
		public static DriveFile GetFileByID( string id )
		{
			return fileIDToFile[id];
		}

		public static async Task<DriveFile> GetFileByIDAsync( string id )
		{
			if( !fileIDToFile.TryGetValue( id, out DriveFile result ) )
			{
				FilesResource.GetRequest request = ( await GetDriveAPIAsync() ).Files.Get( id );
				request.Fields = REQUIRED_FILE_FIELDS;

				try
				{
					DFile file = await request.ExecuteAsync();
					fileIDToFile[file.Id] = result = new DriveFile( file );
				}
				catch( Google.GoogleApiException e )
				{
					if( e.CheckErrorType( "notFound" ) )
						return null;

					throw;
				}
			}

			return result;
		}

		public static bool IsAncestorOf( this DriveFile ancestor, DriveFile child )
		{
			while( !string.IsNullOrEmpty( child.parentID ) )
			{
				string _parentID = child.parentID;
				if( _parentID == ancestor.id )
					return true;

				child = fileIDToFile[_parentID];
			}

			return false;
		}

		public static async Task<DriveFile[]> LoadFileHierarchyAsync( this DriveFile file, string relativeToFolderID = null )
		{
			if( string.IsNullOrEmpty( file.parentID ) )
				return new DriveFile[1] { file };

			List<DriveFile> pathComponents = new List<DriveFile>( 6 ) { file };
			while( !string.IsNullOrEmpty( file.parentID ) && file.parentID != relativeToFolderID )
			{
				file = await GetFileByIDAsync( file.parentID );
				if( file == null )
					break;

				pathComponents.Add( file );
			}

			pathComponents.Reverse();
			return pathComponents.ToArray();
		}

		[InitializeOnLoadMethod]
		private static void ClearThumbnailsOnExit()
		{
			// Clear thumbnail cache when exiting Unity
			EditorApplication.quitting -= ClearThumbnailCache;
			EditorApplication.quitting += ClearThumbnailCache;
		}

		public static void ClearThumbnailCache()
		{
			Directory.Delete( THUMBNAILS_DOWNLOAD_PATH, true );
		}

		private static bool CheckErrorType( this Google.GoogleApiException exception, string expectedErrorType )
		{
			if( exception != null && exception.Error != null && exception.Error.Errors != null )
			{
				foreach( Google.Apis.Requests.SingleError error in exception.Error.Errors )
				{
					if( error.Reason.Equals( expectedErrorType, System.StringComparison.OrdinalIgnoreCase ) )
						return true;
				}
			}

			return false;
		}

		private static async Task<DriveService> GetDriveAPIAsync()
		{
			if( driveAPI == null )
				await InitializeAPIs();

			return driveAPI;
		}

		private static async Task<DriveActivityService> GetDriveActivityAPIAsync()
		{
			if( driveActivityAPI == null )
				await InitializeAPIs();

			return driveActivityAPI;
		}

		private static async Task<PeopleServiceService> GetPeopleAPIAsync()
		{
			if( peopleAPI == null )
				await InitializeAPIs();

			return peopleAPI;
		}

		private static async Task InitializeAPIs()
		{
			ClientSecrets secrets = new ClientSecrets()
			{
				ClientId = GoogleCloudCredentials.Instance.ClientID,
				ClientSecret = GoogleCloudCredentials.Instance.ClientSecret
			};

			string[] scopes = new string[] { DriveService.Scope.DriveReadonly, DriveActivityService.Scope.DriveActivityReadonly, PeopleServiceService.Scope.UserinfoProfile, PeopleServiceService.Scope.ContactsReadonly };
			UserCredential credential = await GoogleWebAuthorizationBroker.AuthorizeAsync( secrets, scopes, "user", CancellationToken.None, new FileDataStore( AUTH_TOKEN_PATH, true ) );
			BaseClientService.Initializer apiInitializer = new BaseClientService.Initializer()
			{
				HttpClientInitializer = credential,
				ApplicationName = "Activity Viewer for Drive",
			};

			driveAPI = new DriveService( apiInitializer );
			driveActivityAPI = new DriveActivityService( apiInitializer );
			peopleAPI = new PeopleServiceService( apiInitializer );

			// Perform a dummy request to verify that our cached access tokens are still valid
			try
			{
				AboutResource.GetRequest request = driveAPI.About.Get();
				request.Fields = "kind";
				await request.ExecuteAsync();
			}
			catch( TokenResponseException e )
			{
				if( e.Error != null )
					Debug.LogWarning( $"Drive access tokens were invalidated, reauthenticating. Reason:\"{e.Error.Error}\", Description:\"{e.Error.ErrorDescription}\", Uri:\"{e.Error.ErrorUri}\"" );
				else
					Debug.LogException( e );

				RevokeAuthentication();
				await InitializeAPIs();
			}
		}

		public static void RevokeAuthentication()
		{
			if( Directory.Exists( AUTH_TOKEN_PATH ) )
				Directory.Delete( AUTH_TOKEN_PATH, true );

			driveAPI = null;
			driveActivityAPI = null;
			peopleAPI = null;
		}
	}
}