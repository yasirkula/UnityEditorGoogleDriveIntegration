namespace DriveBrowser
{
	// Entries are sorted which makes it easier to sort ActivityTreeView by activity type
	public enum FileActivityType { Create, Delete, Edit, Move, Rename, Restore, Unknown };

	public delegate void ActivityEntryDelegate( ActivityEntry activityEntry );

	[System.Serializable]
	public class ActivityEntry
	{
		public FileActivityType type;
		public string fileID;
		public string relativePath;
		public string username;
		public bool isFolder;
		public long size, timeTicks;

		private System.DateTime? m_time;
		public System.DateTime time
		{
			get
			{
				if( !m_time.HasValue )
					m_time = new System.DateTime( timeTicks ).ToLocalTime(); // This DateTime seems to be UTC whereas DriveFile's DateTime is already localized

				return m_time.Value;
			}
		}

		public override string ToString()
		{
			return type + " " + relativePath;
		}
	}
}