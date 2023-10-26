using OsmSharp.API;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChangesetLimiter
{
    public class OsmAccountInfo
    {
        static OsmAccountInfo()
        {
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            if (File.Exists("accounts.json"))
                _users = JsonSerializer.Deserialize<Dictionary<long, OsmAccountInfo>>(System.IO.File.ReadAllText("accounts.json"));
        }

        private static HttpClient _httpClient = new();
        private static Dictionary<long, OsmAccountInfo> _users = new();

        public long Id { get; set; }
        public int ChangesetsCount { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime FetchedDate { get; set; }

        public static async Task FetchUsers(long[] ids)
        {
            var dateTimeNow = DateTime.UtcNow;
            foreach (var user in _users.Values.ToArray())
            {
                if (dateTimeNow - user.FetchedDate > TimeSpan.FromDays(1))
                    _users.Remove(user.Id);
            }
            var newUsersToFetch = ids.Except(_users.Keys).ToArray();
            if (newUsersToFetch.Length == 0)
                return;
            var response = await _httpClient.GetFromJsonAsync<UsersApiResponse>($"https://www.openstreetmap.org/api/0.6/users?users={string.Join(",", newUsersToFetch)}");
            if (response == null)
                return;
            foreach (var user in response.users)
            {
                _users.Add(user.user.id, new OsmAccountInfo() {
                    Id = user.user.id,
                    CreatedDate = user.user.account_created,
                    ChangesetsCount = user.user.changesets.count,
                    FetchedDate = dateTimeNow
                });
            }
            foreach (var nonExistingUserId in newUsersToFetch.Except(_users.Keys))
            {
                // If user is not found we assume because it was deleted, assume the worst, treat it as a new user, with zero changesets and a creation date in the future
                _users.Add(nonExistingUserId, new OsmAccountInfo() { Id = nonExistingUserId, ChangesetsCount = 0, CreatedDate = DateTime.MaxValue, FetchedDate = DateTime.MaxValue });
            }
            File.WriteAllText("accounts.json", JsonSerializer.Serialize(_users));
        }

        internal static OsmAccountInfo GetUserInfo(long userId)
        {
            return _users[userId];
        }
    }
}
