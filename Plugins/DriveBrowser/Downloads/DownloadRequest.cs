using UnityEditor;
#if UNITY_2020_2_OR_NEWER
using UnityEditor.AssetImporters;
#else
using UnityEditor.Experimental.AssetImporters;
#endif
using UnityEngine;
using System.IO;

namespace DriveBrowser
{
	[System.Serializable]
	public class DownloadRequest
	{
		public string[] fileIDs;
		public string path;
	}

	[ScriptedImporter( 1, EXTENSION )]
	public class DownloadRequestImporter : ScriptedImporter
	{
		public const string EXTENSION = "drivedl";
		public const string DOWNLOAD_REQUEST_TEMP_PATH = "Library/DriveDownload." + DownloadRequestImporter.EXTENSION;

		public override void OnImportAsset( AssetImportContext ctx )
		{
			string assetPath = ctx.assetPath;

			DownloadRequest downloadRequest = JsonUtility.FromJson<DownloadRequest>( File.ReadAllText( assetPath ) );
			downloadRequest.path = Path.GetDirectoryName( assetPath );

			EditorApplication.delayCall += () =>
			{
				// Can't delete the asset immediately, wait for 1 frame
				AssetDatabase.DeleteAsset( assetPath );

				// Initiate the download
				downloadRequest.DownloadAsync();
			};
		}
	}
}