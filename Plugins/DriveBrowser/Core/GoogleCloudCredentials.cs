using System.IO;
using UnityEditor;
using UnityEngine;

namespace DriveBrowser
{
	[HelpURL( "https://github.com/yasirkula/UnityEditorGoogleDriveIntegration/wiki/Creating-Google-Cloud-Project" )]
	public class GoogleCloudCredentials : ScriptableObject
	{
		private const string INITIAL_SAVE_PATH = "Assets/Plugins/DriveBrowser/GoogleCloudCredentials.asset";

		public string ClientID, ClientSecret;

		private static GoogleCloudCredentials m_instance;
		public static GoogleCloudCredentials Instance
		{
			get
			{
				if( !m_instance )
				{
					string[] instances = AssetDatabase.FindAssets( "t:GoogleCloudCredentials" );
					if( instances != null && instances.Length > 0 )
						m_instance = AssetDatabase.LoadAssetAtPath<GoogleCloudCredentials>( AssetDatabase.GUIDToAssetPath( instances[0] ) );

					if( !m_instance )
					{
						Directory.CreateDirectory( Path.GetDirectoryName( INITIAL_SAVE_PATH ) );

						AssetDatabase.CreateAsset( CreateInstance<GoogleCloudCredentials>(), INITIAL_SAVE_PATH );
						AssetDatabase.SaveAssets();
						m_instance = AssetDatabase.LoadAssetAtPath<GoogleCloudCredentials>( INITIAL_SAVE_PATH );

						Debug.Log( "Created Google Cloud credentials file at " + INITIAL_SAVE_PATH + ". You can move this file around freely.", m_instance );
					}
				}

				return m_instance;
			}
		}
	}

	[CustomEditor( typeof( GoogleCloudCredentials ) )]
	public class GoogleCloudCredentialsEditor : Editor
	{
		public override void OnInspectorGUI()
		{
			GoogleCloudCredentials credentials = (GoogleCloudCredentials) target;
			bool credentialsMissing = ( string.IsNullOrEmpty( credentials.ClientID ) || string.IsNullOrEmpty( credentials.ClientSecret ) );

			if( credentialsMissing )
				EditorGUILayout.HelpBox( "Fill in the credentials first!", MessageType.Error );
			else
			{
				EditorGUILayout.HelpBox(
					"Anyone who has access to these credentials can use them to call Drive APIs using your daily quota.\n\n" +
					"It is not a major deal since these credentials can be regenerated but if you don't want others to see/use your credentials, consider excluding this file from your repository (e.g. '.gitignore').\n\n" +
					"In that case, other people who have access to this project will have to generate their own credentials (i.e. create their own Google Cloud projects).", MessageType.Info );
			}

			serializedObject.Update();
			DrawPropertiesExcluding( serializedObject, "m_Script" );
			serializedObject.ApplyModifiedProperties();

			if( !credentialsMissing )
			{
				EditorGUILayout.Space();

				if( GUILayout.Button( "Open Drive Browser" ) )
					FileBrowser.Initialize();
			}
		}
	}
}