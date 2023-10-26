using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChangesetLimiter
{
    public class MemcachedUserInfo
    {
        public DateTime LastTimeReset { get; set; }

        public int ChangesetsCount { get; set; }

        public int CreatedNodes { get; set; }
        public int ModifiedNodes { get; set; }
        public int DeletedNodes { get; set; }

        public int CreatedWays { get; set; }
        public int ModifiedWays { get; set; }
        public int DeletedWays { get; set; }

        public int CreatedRelationships { get; set; }
        public int ModifiedRelationships { get; set; }
        public int DeletedRelationships { get; set; }
    }

    public static class Memcached
    {
        static Dictionary<long, MemcachedUserInfo> _storage = new();
        public static bool TryGet(long userId, [NotNullWhen(true)] out MemcachedUserInfo? userInfo)
        {
            return _storage.TryGetValue(userId, out userInfo);
        }

        public static void Set(long userId, MemcachedUserInfo userInfo)
        {
            _storage[userId] = userInfo;
        }
    }
}
