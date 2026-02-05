using UnityEditor;
using UnityEngine;

namespace DriveBrowser
{
    [HelpURL("https://github.com/yasirkula/UnityEditorGoogleDriveIntegration/wiki/Creating-Google-Cloud-Project")]
    [FilePath("UserSettings/DriveBrowserSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class Settings : ScriptableSingleton<Settings>
    {
        [Delayed]
        public string ClientID, ClientSecret;

        [Tooltip("If enabled, files and folders that are shared with you by an organization or workspace will also be shown. If Drive Browser window is open while toggling this option, you need to right click its tab and select \"Refresh Root\" for the change to take effect.")]
        public bool ShowSharedDriveFiles = true;

        /// <see cref="ScriptableSingleton{T}"/>'s default is <see cref="HideFlags.HideAndDontSave"/> which isn't editable in Inspector so we change it manually.
        protected void OnEnable() => hideFlags = HideFlags.DontSave;

        /// <summary>Since this asset is no longer serialized in Assets folder, it isn't auto-saved by AssetDatabase. So we need to save it manually when a change is made.</summary>
        protected void OnValidate() => EditorApplication.delayCall += () => Save(true);
    }

    [CustomEditor(typeof(Settings))]
    public class SettingsEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            Settings credentials = (Settings)target;
            bool credentialsMissing = (string.IsNullOrEmpty(credentials.ClientID) || string.IsNullOrEmpty(credentials.ClientSecret));
            if (credentialsMissing)
                EditorGUILayout.HelpBox("Fill in the credentials first!", MessageType.Error);

            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script");
            serializedObject.ApplyModifiedProperties();

            if (!credentialsMissing)
            {
                EditorGUILayout.Space();

                if (GUILayout.Button("Open Drive Browser"))
                    FileBrowser.Initialize();
            }
        }
    }
}
