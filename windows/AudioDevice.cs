using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioShare
{
    public class AudioDevice
    {
        public string Name { get; set; }
        public string ID { get; set; }

        public AudioDevice(string id, string name)
        {
            Name = name;
            ID = id;
        }
    }
}
