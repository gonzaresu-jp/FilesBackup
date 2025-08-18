namespace FilesBackup.Models
{
    public class IgnoreConfig
    {
        public List<string> Paths { get; set; } = new();
        public List<string> Extensions { get; set; } = new(); // .tmp, .logなど
    }

}

