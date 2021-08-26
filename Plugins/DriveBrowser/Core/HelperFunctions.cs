using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DriveBrowser
{
	public static class HelperFunctions
	{
		public class HeaderColumn
		{
			public readonly string label;
			public readonly bool autoResize, sortedAscending;
			public readonly float minWidth, maxWidth, width;

			public HeaderColumn( string label, bool autoResize, bool sortedAscending, float minWidth, float maxWidth, float width )
			{
				this.label = label;
				this.autoResize = autoResize;
				this.sortedAscending = sortedAscending;
				this.minWidth = minWidth;
				this.maxWidth = maxWidth;
				this.width = width;
			}
		}

		private const float WINDOW_REPOSITION_PADDING_TOP = 30f;

		private static bool assemblyLockedHintShown;
		private static MethodInfo screenFittedRectGetter;

		public static void InitiateDragDropDownload( string[] fileIDs )
		{
			if( fileIDs == null || fileIDs.Length == 0 )
				return;

			DownloadRequest downloadRequest = new DownloadRequest() { fileIDs = fileIDs };
			File.WriteAllText( DownloadRequestImporter.DOWNLOAD_REQUEST_TEMP_PATH, JsonUtility.ToJson( downloadRequest ) );

			DragAndDrop.PrepareStartDrag();
			DragAndDrop.paths = new string[1] { DownloadRequestImporter.DOWNLOAD_REQUEST_TEMP_PATH };
			DragAndDrop.StartDrag( "Download " + ( fileIDs.Length > 1 ? "Multiple" : DriveAPI.GetFileByID( fileIDs[0] ).name ) );
		}

		public static void MoveWindowOverCursor( EditorWindow window, float preferredHeight )
		{
			if( Event.current == null )
			{
				Debug.LogError( "MoveOverCursor must be called from OnGUI" );
				return;
			}

			Rect windowRect = window.position;
			windowRect.height = preferredHeight + WINDOW_REPOSITION_PADDING_TOP;
			windowRect.position = GUIUtility.GUIToScreenPoint( Event.current.mousePosition ) - new Vector2( windowRect.width * 0.5f, windowRect.height * 1.15f );

			// If we don't call FitRectToScreen, EditorWindow can actually spawn outside of the screen
			if( screenFittedRectGetter == null )
				screenFittedRectGetter = typeof( EditorWindow ).Assembly.GetType( "UnityEditor.ContainerWindow" ).GetMethod( "FitRectToScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static );

			windowRect = (Rect) screenFittedRectGetter.Invoke( null, new object[3] { windowRect, true, true } );
			windowRect.height = preferredHeight;
			windowRect.y += WINDOW_REPOSITION_PADDING_TOP;

			window.position = windowRect;
		}

		public static string[] GetFileIDs( this DriveFile[] files )
		{
			string[] fileIDs = new string[files.Length];
			for( int i = 0; i < files.Length; i++ )
				fileIDs[i] = files[i].id;

			return fileIDs;
		}

		public static string[] GetFileIDs( this ActivityEntry[] entries )
		{
			string[] fileIDs = new string[entries.Length];
			for( int i = 0; i < entries.Length; i++ )
				fileIDs[i] = entries[i].fileID;

			return fileIDs;
		}

		public static void SerializeToArray<T>( this Dictionary<T, T> dictionary, out T[] array )
		{
			array = new T[dictionary.Count * 2];

			int index = 0;
			foreach( KeyValuePair<T, T> kvPair in dictionary )
			{
				array[index] = kvPair.Key;
				array[index + 1] = kvPair.Value;

				index += 2;
			}
		}

		public static void DeserializeFromArray<T>( this Dictionary<T, T> dictionary, T[] array )
		{
			if( array != null )
			{
				for( int i = 0; i < array.Length; i += 2 )
					dictionary[array[i]] = array[i + 1];
			}
		}

		public static void SerializeToArray<K, V>( this Dictionary<K, V> dictionary, out K[] keys, out V[] values )
		{
			keys = new K[dictionary.Count];
			values = new V[dictionary.Count];

			int index = 0;
			foreach( KeyValuePair<K, V> kvPair in dictionary )
			{
				keys[index] = kvPair.Key;
				values[index] = kvPair.Value;

				index++;
			}
		}

		public static void DeserializeFromArray<K, V>( this Dictionary<K, V> dictionary, K[] keys, V[] values )
		{
			if( keys != null && values != null )
			{
				for( int i = 0; i < keys.Length; i++ )
					dictionary[keys[i]] = values[i];
			}
		}

		public static MultiColumnHeader GenerateMultiColumnHeader( ref MultiColumnHeaderState headerState, int defaultSortedColumnIndex, params HeaderColumn[] columns )
		{
			MultiColumnHeaderState.Column[] _columns = new MultiColumnHeaderState.Column[columns.Length];
			for( int i = 0; i < columns.Length; i++ )
			{
				_columns[i] = new MultiColumnHeaderState.Column()
				{
					headerContent = new GUIContent( columns[i].label, columns[i].label ),
					allowToggleVisibility = true,
					autoResize = columns[i].autoResize,
					canSort = true,
					sortedAscending = columns[i].sortedAscending,
					headerTextAlignment = TextAlignment.Left,
					sortingArrowAlignment = TextAlignment.Center,
				};

				if( columns[i].minWidth > 0f )
					_columns[i].minWidth = columns[i].minWidth;
				if( columns[i].maxWidth > 0f )
					_columns[i].maxWidth = columns[i].maxWidth;
				if( columns[i].width > 0f )
					_columns[i].width = columns[i].width;
			}

			// IDK most of the technical stuff done here. Credit: https://docs.unity3d.com/Manual/TreeViewAPI.html
			MultiColumnHeaderState newHeaderState = new MultiColumnHeaderState( _columns );

			if( MultiColumnHeaderState.CanOverwriteSerializedFields( headerState, newHeaderState ) )
				MultiColumnHeaderState.OverwriteSerializedFields( headerState, newHeaderState );

			MultiColumnHeader multiColumnHeader = new MultiColumnHeader( newHeaderState );
			if( headerState == null ) // First initialization
			{
				multiColumnHeader.ResizeToFit();
				multiColumnHeader.sortedColumnIndex = defaultSortedColumnIndex;
			}

			headerState = newHeaderState;

			return multiColumnHeader;
		}

		// Credit: https://stackoverflow.com/a/10520086/2373034
		public static string CalculateMD5Hash( string filePath )
		{
			using( MD5 md5 = MD5.Create() )
			{
				using( FileStream stream = File.OpenRead( filePath ) )
				{
					byte[] hash = md5.ComputeHash( stream );
					return System.BitConverter.ToString( hash ).Replace( "-", "" ).ToLowerInvariant();
				}
			}
		}

		public static void LockAssemblyReload()
		{
			assemblyLockedHintShown = false;

			EditorApplication.LockReloadAssemblies();
			EditorApplication.update -= EnforceAssemblyLock;
			EditorApplication.update += EnforceAssemblyLock;
		}

		public static void UnlockAssemblyReload()
		{
			EditorApplication.update -= EnforceAssemblyLock;
			EditorApplication.UnlockReloadAssemblies();
		}

		private static void EnforceAssemblyLock()
		{
			if( EditorApplication.isPlayingOrWillChangePlaymode )
			{
				EditorApplication.isPlaying = false;
				Debug.LogWarning( "Can't enter Play mode while an asynchronous Drive operation is in progress!" );
			}

			if( !assemblyLockedHintShown && EditorApplication.isCompiling )
			{
				assemblyLockedHintShown = true;
				Debug.LogWarning( "Can't reload assemblies while an asynchronous Drive operation is in progress!" );
			}
		}
	}
}