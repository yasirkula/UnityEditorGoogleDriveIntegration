using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace DriveBrowser
{
	public static class FilePreviewPopup
	{
		private class PopupContent : PopupWindowContent
		{
			private const float LOADING_BAR_SIZE = 32f;

			public override Vector2 GetWindowSize()
			{
				return new Vector2( 128f, 128f );
			}

			public override void OnGUI( Rect rect )
			{
				if( loadingThumbnail )
				{
					Rect loadingBarRect = new Rect( rect.center - new Vector2( LOADING_BAR_SIZE * 0.5f, LOADING_BAR_SIZE * 0.5f ), new Vector2( LOADING_BAR_SIZE, LOADING_BAR_SIZE ) );
					GUI.Label( loadingBarRect, loadingBar );
				}
				else if( thumbnail )
					GUI.DrawTexture( rect, thumbnail, ScaleMode.ScaleToFit );
			}

			public override void OnClose()
			{
				if( cancellationTokenSource != null )
				{
					cancellationTokenSource.Cancel();
					cancellationTokenSource = null;
				}
			}
		}

		// Credit: https://github.com/Unity-Technologies/UnityCsReference/blob/61f92bd79ae862c4465d35270f9d1d57befd1761/Editor/Mono/InternalEditorUtility.cs#L90-L111
		private static GUIContent[] loadingBarImages;
		private static GUIContent loadingBar
		{
			get
			{
				if( loadingBarImages == null )
				{
					loadingBarImages = new GUIContent[12];
					for( int i = 0; i < 12; i++ )
					{
						loadingBarImages[i] = new GUIContent { image = EditorGUIUtility.IconContent( "WaitSpin" + i.ToString( "00" ) ).image };
						loadingBarImages[i].image.hideFlags = HideFlags.HideAndDontSave;
						loadingBarImages[i].image.name = "Spinner";
					}
				}

				int frame = (int) Mathf.Repeat( Time.realtimeSinceStartup * 10, 11.99f );
				return loadingBarImages[frame];
			}
		}

		private static readonly PopupContent popupContent = new PopupContent();
		private static EditorWindow ActiveWindow { get { return popupContent.editorWindow; } }
		//private static EditorWindow ActiveWindow { get { return activeWindowGetter.GetValue( null ) as EditorWindow; } }

		private static Texture2D thumbnail;
		private static bool loadingThumbnail;
		private static string loadingThumbnailForFileID;

		private static CancellationTokenSource cancellationTokenSource;

		private static readonly System.Type popupWindowType = typeof( EditorWindow ).Assembly.GetType( "UnityEditor.PopupWindowWithoutFocus" );
		private static readonly System.Type popupLocationType = typeof( EditorWindow ).Assembly.GetType( "UnityEditor.PopupLocation" );

		//private static readonly FieldInfo activeWindowGetter = popupWindowType.GetField( "s_PopupWindowWithoutFocus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static );
		private static readonly MethodInfo showWindowFunction = popupWindowType.GetMethod( "Show", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new System.Type[3] { typeof( Rect ), typeof( PopupWindowContent ), popupLocationType.MakeArrayType() }, null );

		private static System.Array preferredPopupLocations;

		public static void Show( Rect rect, DriveFile file )
		{
			if( file == null || string.IsNullOrEmpty( file.id ) || file.isFolder || file.name.EndsWith( ".meta", System.StringComparison.OrdinalIgnoreCase ) )
				return;

			if( preferredPopupLocations == null )
			{
				preferredPopupLocations = System.Array.CreateInstance( popupLocationType, 2 );
				// Omitted Right because preview doesn't want to show up at right in 'Drive Browser' window for unknown reasons
				//preferredPopupLocations.SetValue( System.Enum.Parse( popupLocationType, "Right" ), 0 );
				preferredPopupLocations.SetValue( System.Enum.Parse( popupLocationType, "Left" ), 0 );
				preferredPopupLocations.SetValue( System.Enum.Parse( popupLocationType, "Below" ), 1 );
			}

			showWindowFunction.Invoke( null, new object[3] { rect, popupContent, preferredPopupLocations } );
			ShowThumbnailAsync( file );

			EditorApplication.update -= RepaintPopupContentWhileLoading;
			EditorApplication.update += RepaintPopupContentWhileLoading;
		}

		public static void Hide()
		{
			EditorWindow activeWindow = ActiveWindow;
			if( activeWindow )
			{
				activeWindow.Close();

				if( cancellationTokenSource != null )
				{
					cancellationTokenSource.Cancel();
					cancellationTokenSource = null;
				}

				EditorApplication.update -= RepaintPopupContentWhileLoading;
			}
		}

		public static void Dispose()
		{
			if( thumbnail )
			{
				Object.DestroyImmediate( thumbnail );
				thumbnail = null;
			}

			if( cancellationTokenSource != null )
			{
				cancellationTokenSource.Cancel();
				cancellationTokenSource = null;
			}

			Hide();
		}

		private static async void ShowThumbnailAsync( DriveFile file )
		{
			if( cancellationTokenSource != null )
			{
				cancellationTokenSource.Cancel();
				cancellationTokenSource = null;
			}

			loadingThumbnail = true;
			loadingThumbnailForFileID = file.id;

			using( CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource() )
			{
				cancellationTokenSource = _cancellationTokenSource;
				string thumbnailPath = await file.GetThumbnailAsync( _cancellationTokenSource.Token );
				if( cancellationTokenSource == _cancellationTokenSource )
					cancellationTokenSource = null;

				// We may have requested another thumbnail before this thumbnail was downloaded from the server
				if( loadingThumbnailForFileID == file.id )
				{
					loadingThumbnail = false;

					if( string.IsNullOrEmpty( thumbnailPath ) )
						Hide();
					else
					{
						if( !thumbnail )
							thumbnail = new Texture2D( 256, 256, TextureFormat.RGBA32, false ) { hideFlags = HideFlags.HideAndDontSave };

						thumbnail.LoadImage( File.ReadAllBytes( thumbnailPath ) );
					}

					EditorWindow activeWindow = ActiveWindow;
					if( activeWindow )
						activeWindow.Repaint();
				}
			}
		}

		private static void RepaintPopupContentWhileLoading()
		{
			EditorWindow activeWindow = ActiveWindow;
			if( activeWindow && loadingThumbnail )
				activeWindow.Repaint();
			else
				EditorApplication.update -= RepaintPopupContentWhileLoading;
		}
	}
}