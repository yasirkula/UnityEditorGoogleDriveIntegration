using DFile = Google.Apis.Drive.v3.Data.File;

namespace DriveBrowser
{
	public enum FolderChildrenState { Unknown = 0, HasChildren = 1, NoChildren = 2 };

	[System.Serializable]
	public class DriveFile
	{
		public string id, name, parentID;
		public long size, modifiedTimeTicks;
		public bool isFolder;
		public string[] children = new string[0];
		public FolderChildrenState childrenState;

		private System.DateTime? m_modifiedTime;
		public System.DateTime modifiedTime
		{
			get
			{
				if( !m_modifiedTime.HasValue )
					m_modifiedTime = new System.DateTime( modifiedTimeTicks );

				return m_modifiedTime.Value;
			}
		}

		public DriveFile( DFile file )
		{
			id = file.Id;
			name = file.Name;
			parentID = ( file.Parents != null && file.Parents.Count > 0 ) ? file.Parents[0] : null;
			size = file.Size ?? 0L;
			modifiedTimeTicks = file.ModifiedTime.HasValue ? file.ModifiedTime.Value.Ticks : 0L;
			isFolder = ( file.MimeType == "application/vnd.google-apps.folder" );
			childrenState = ( isFolder ? FolderChildrenState.Unknown : FolderChildrenState.NoChildren );
		}

		public override string ToString()
		{
			return name;
		}
	}
}