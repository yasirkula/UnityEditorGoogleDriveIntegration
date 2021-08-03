using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace DriveBrowser
{
	public class DownloadProgressViewer : EditorWindow
	{
		[System.Serializable]
		private class DownloadedFile
		{
			public DriveFile file;
			public long downloadedBytes;
		}

		private int totalFileCount, downloadedFileCount;
		private CancellationTokenSource cancellationTokenSource;
		private List<DownloadedFile> downloadedFiles = new List<DownloadedFile>( 4 );

		private bool shouldRepositionSelf = true;

		private Vector2 scrollPos;

		public static DownloadProgressViewer Initialize( int totalFileCount, CancellationTokenSource cancellationTokenSource )
		{
			DownloadProgressViewer window = CreateInstance<DownloadProgressViewer>();
			window.titleContent = new GUIContent( "Download Progress" );
			window.minSize = new Vector2( 200f, 80f );
			window.maxSize = new Vector2( 500f, 150f );

			window.cancellationTokenSource = cancellationTokenSource;
			window.totalFileCount = totalFileCount;

			window.ShowUtility();
			window.Repaint();

			return window;
		}

		private void OnDisable()
		{
			if( cancellationTokenSource != null && !cancellationTokenSource.IsCancellationRequested )
			{
				EditorUtility.DisplayDialog( "Warning", "Closing this window automatically cancels the download.", "OK" );
				cancellationTokenSource.Cancel();
			}
		}

		public void AddDownload( DriveFile downloadedFile )
		{
			downloadedFiles.Add( new DownloadedFile() { file = downloadedFile } );
			EditorApplication.delayCall += Repaint; // See: SetProgress
		}

		public void RemoveDownload( DriveFile downloadedFile )
		{
			downloadedFileCount += downloadedFiles.RemoveAll( ( download ) => download.file == downloadedFile );
			EditorApplication.delayCall += Repaint; // See: SetProgress
		}

		public void SetProgress( DriveFile downloadedFile, long downloadedBytes )
		{
			foreach( DownloadedFile download in downloadedFiles )
			{
				if( download.file == downloadedFile )
				{
					download.downloadedBytes = downloadedBytes;
					EditorApplication.delayCall += Repaint; // SetProgress can be called from a separate thread whereas Repaint must be called from the main thread

					return;
				}
			}
		}

		public void IncrementTotalFileCount( int delta )
		{
			totalFileCount += delta;
		}

		public void DownloadCompleted()
		{
			cancellationTokenSource = null;
			Repaint();
		}

		private void OnGUI()
		{
			if( shouldRepositionSelf )
			{
				shouldRepositionSelf = false;
				HelperFunctions.MoveWindowOverCursor( this, maxSize.y );
			}

			if( cancellationTokenSource != null )
			{
				EditorGUILayout.LabelField( string.Concat( "Downloaded: ", downloadedFileCount.ToString(), "/", totalFileCount.ToString() ) );

				EditorGUILayout.Space();

				scrollPos = EditorGUILayout.BeginScrollView( scrollPos );

				foreach( DownloadedFile download in downloadedFiles )
				{
					string progressLabel;
					if( download.file.size == 0L )
						progressLabel = download.file.name;
					else
						progressLabel = string.Concat( download.file.name, " (", EditorUtility.FormatBytes( download.downloadedBytes ), " / ", EditorUtility.FormatBytes( download.file.size ), ")" );

					float progress = ( download.file.size == 0L ) ? 0f : (float) ( (double) download.downloadedBytes / download.file.size );
					EditorGUI.ProgressBar( EditorGUILayout.GetControlRect( false, EditorGUIUtility.singleLineHeight ), progress, progressLabel );
				}

				EditorGUILayout.EndScrollView();

				GUILayout.FlexibleSpace();

				GUI.enabled = !cancellationTokenSource.IsCancellationRequested;
				if( GUILayout.Button( "Cancel" ) )
					cancellationTokenSource.Cancel();
				GUI.enabled = true;
			}
			else
			{
				EditorGUILayout.LabelField( "Download finished" );

				GUILayout.FlexibleSpace();

				if( GUILayout.Button( "Close" ) )
					Close();
			}

			EditorGUILayout.Space();
		}
	}
}