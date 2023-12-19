namespace AudioShare
{
    public class NamePair
    {
        public string Name { get; set; }
        public string ID { get; set; }

        public NamePair(string id, string name)
        {
            Name = name;
            ID = id;
        }
    }
}
