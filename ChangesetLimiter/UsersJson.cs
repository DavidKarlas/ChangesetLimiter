using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChangesetLimiter
{

    public class UsersApiResponse
    {
        public string version { get; set; }
        public string generator { get; set; }
        public string copyright { get; set; }
        public string attribution { get; set; }
        public string license { get; set; }
        public UserDummy[] users { get; set; }
    }

    public class UserDummy
    {
        public User user { get; set; }
    }

    public class User
    {
        public long id { get; set; }
        public string display_name { get; set; }
        public DateTime account_created { get; set; }
        public string description { get; set; }
        public Contributor_Terms contributor_terms { get; set; }
        public object[] roles { get; set; }
        public Changesets changesets { get; set; }
        public Traces traces { get; set; }
        public Blocks blocks { get; set; }
        public Img img { get; set; }

        public class Contributor_Terms
        {
            public bool agreed { get; set; }
        }

        public class Changesets
        {
            public int count { get; set; }
        }

        public class Traces
        {
            public int count { get; set; }
        }

        public class Blocks
        {
            public Received received { get; set; }
        }

        public class Received
        {
            public int count { get; set; }
            public int active { get; set; }
        }

        public class Img
        {
            public string href { get; set; }
        }
    }

}
