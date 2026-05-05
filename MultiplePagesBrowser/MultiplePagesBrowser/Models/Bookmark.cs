namespace MultiplePagesBrowser.Models
{
    public class Bookmark
    {
        public string Title { get; set; } = string.Empty;
        public string Url   { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }
}
