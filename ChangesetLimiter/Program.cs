using ChangesetLimiter;
using OsmSharp;
using OsmSharp.Changesets;
using OsmSharp.Replication;
using System.IO.Compression;
using System.Text;
using System.Xml.Serialization;

var ThreadLocalXmlSerializer = new ThreadLocal<XmlSerializer>(() => new XmlSerializer(typeof(OsmChange)));
var httpClient = new HttpClient();
var replicationEnumerator = await ReplicationConfig.Hourly.GetDiffEnumerator(DateTime.UtcNow.AddDays(-1));
if (replicationEnumerator == null)
{
    throw new Exception();
}
var reasons = new List<string>();
var rateLimitedAccounts = new Dictionary<long, RateLimitedAccount>();
ReplicationState? replicationState = null;
var users = new HashSet<long>();
var duplicatedChangesets = new HashSet<long>();
while (true)
{
    try
    {
        while (await replicationEnumerator.MoveNext())
        {
            replicationState = replicationEnumerator.State;
            Console.WriteLine("Downloading changeset " + replicationState.StartTimestamp + " - " + replicationState.EndTimestamp);
            var replicationChangeset = DownloadChangeset(replicationState.Config, replicationState.SequenceNumber);
            var changesets = ExtractRawChangesets(replicationChangeset!);
            await OsmAccountInfo.FetchUsers(changesets.Select(c => c.UserId).Distinct().ToArray());
            foreach (var changeset in changesets)
            {
                users.Add(changeset.UserId);
                if (!Memcached.TryGet(changeset.UserId, out var memcachedUserInfo) || memcachedUserInfo.LastTimeReset < changeset.Timestamp.AddDays(-1))
                {
                    memcachedUserInfo = new MemcachedUserInfo();
                    memcachedUserInfo.LastTimeReset = changeset.Timestamp;
                }
                if (duplicatedChangesets.Add(changeset.ChangesetId))
                    memcachedUserInfo.ChangesetsCount++;

                memcachedUserInfo.CreatedNodes += changeset.CreatedNodes;
                memcachedUserInfo.ModifiedNodes += changeset.ModifiedNodes;
                memcachedUserInfo.DeletedNodes += changeset.DeletedNodes;

                memcachedUserInfo.CreatedWays += changeset.CreatedWays;
                memcachedUserInfo.ModifiedWays += changeset.ModifiedWays;
                memcachedUserInfo.DeletedWays += changeset.DeletedWays;

                memcachedUserInfo.CreatedRelationships += changeset.CreatedRelationships;
                memcachedUserInfo.ModifiedRelationships += changeset.ModifiedRelationships;
                memcachedUserInfo.DeletedRelationships += changeset.DeletedRelationships;

                Memcached.Set(changeset.UserId, memcachedUserInfo);
                var userInfo = OsmAccountInfo.GetUserInfo(changeset.UserId);
                var limits = new LimitsPerDay(userInfo.CreatedDate, userInfo.ChangesetsCount, changeset.Timestamp);
                if (limits.ChangesetsCountLimit < memcachedUserInfo.ChangesetsCount)
                    reasons.Add($"Made {memcachedUserInfo.ChangesetsCount} changesets in 24 hours, limit is {limits.ChangesetsCountLimit}.");
                if (limits.CreatedWaysLimit < memcachedUserInfo.CreatedWays)
                    reasons.Add($"Created {memcachedUserInfo.CreatedWays} ways in 24 hours, limit is {limits.CreatedWaysLimit}.");
                if (limits.ModifiedWaysLimit < memcachedUserInfo.ModifiedWays)
                    reasons.Add($"Modified {memcachedUserInfo.ModifiedWays} ways in 24 hours, limit is {limits.ModifiedWaysLimit}.");
                if (limits.DeletedWaysLimit < memcachedUserInfo.DeletedWays)
                    reasons.Add($"Deleted {memcachedUserInfo.DeletedWays} ways in 24 hours, limit is {limits.DeletedWaysLimit}.");
                if (limits.CreatedNodesLimit < memcachedUserInfo.CreatedNodes)
                    reasons.Add($"Created {memcachedUserInfo.CreatedNodes} nodes in 24 hours, limit is {limits.CreatedNodesLimit}.");
                if (limits.ModifiedNodesLimit < memcachedUserInfo.ModifiedNodes)
                    reasons.Add($"Modified {memcachedUserInfo.ModifiedNodes} nodes in 24 hours, limit is {limits.ModifiedNodesLimit}.");
                if (limits.DeletedNodesLimit < memcachedUserInfo.DeletedNodes)
                    reasons.Add($"Deleted {memcachedUserInfo.DeletedNodes} nodes in 24 hours, limit is {limits.DeletedNodesLimit}.");
                if (limits.CreatedRelationshipsLimit < memcachedUserInfo.CreatedRelationships)
                    reasons.Add($"Created {memcachedUserInfo.CreatedRelationships} relationships in 24 hours, limit is {limits.CreatedRelationshipsLimit}.");
                if (limits.ModifiedRelationshipsLimit < memcachedUserInfo.ModifiedRelationships)
                    reasons.Add($"Modified {memcachedUserInfo.ModifiedRelationships} relationships in 24 hours, limit is {limits.ModifiedRelationshipsLimit}.");
                if (limits.DeletedRelationshipsLimit < memcachedUserInfo.DeletedRelationships)
                    reasons.Add($"Deleted {memcachedUserInfo.DeletedRelationships} relationships in 24 hours, limit is {limits.DeletedRelationshipsLimit}.");

                if (reasons.Count > 0)
                {
                    if (!rateLimitedAccounts.TryGetValue(changeset.UserId, out var blockedChangesetsForUser))
                    {
                        rateLimitedAccounts[changeset.UserId] = blockedChangesetsForUser = new() {
                            UserId = changeset.UserId,
                            Username = changeset.Username,
                            Changesets = new(),
                        };
                        Console.WriteLine("https://www.openstreetmap.org/changeset/" + changeset.ChangesetId + " " + string.Join(" - ", reasons));
                    }
                    blockedChangesetsForUser.Changesets.Add(new() {
                        Reasons = reasons,
                        Id = changeset.ChangesetId,
                        Timestamp = changeset.Timestamp
                    });
                    reasons = new List<string>();
                }
            }
        }
        if (replicationEnumerator.Config.IsHourly)
        {
            replicationEnumerator = await ReplicationConfig.Minutely.GetDiffEnumerator(replicationState.EndTimestamp);
            if (replicationEnumerator == null)
            {
                throw new Exception();
            }
            continue;
        }
        var ageLimit = DateTime.UtcNow.AddDays(-1);
        foreach (var account in rateLimitedAccounts.Values.ToArray())
        {
            account.Changesets.RemoveAll(c => c.Timestamp < ageLimit);
            if (account.Changesets.Count == 0)
            {
                rateLimitedAccounts.Remove(account.UserId);
            }
        }
        RateLimitUploader.Upload(rateLimitedAccounts.Values.ToArray());
        Console.WriteLine("Rate:" + rateLimitedAccounts.Values.Count / (double)users.Count + " " + rateLimitedAccounts.Values.Count + " " + users.Count);
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
    }
    await Task.Delay(TimeSpan.FromMinutes(1));
}

RawChangeset[] ExtractRawChangesets(OsmChange changeset)
{
    var changesets = new Dictionary<long, RawChangeset>();
    foreach (var item in changeset.Create)
    {
        var rawChangeset = GetRawChangeset(changesets, item);
        switch (item.Type)
        {
            case OsmGeoType.Node:
                rawChangeset.CreatedNodes++;
                break;
            case OsmGeoType.Way:
                rawChangeset.CreatedWays++;
                break;
            case OsmGeoType.Relation:
                rawChangeset.CreatedRelationships++;
                break;
        }
    }
    foreach (var item in changeset.Modify)
    {
        var rawChangeset = GetRawChangeset(changesets, item);
        switch (item.Type)
        {
            case OsmGeoType.Node:
                rawChangeset.ModifiedNodes++;
                break;
            case OsmGeoType.Way:
                rawChangeset.ModifiedWays++;
                break;
            case OsmGeoType.Relation:
                rawChangeset.ModifiedRelationships++;
                break;
        }
    }
    foreach (var item in changeset.Delete)
    {
        var rawChangeset = GetRawChangeset(changesets, item);
        switch (item.Type)
        {
            case OsmGeoType.Node:
                rawChangeset.DeletedNodes++;
                break;
            case OsmGeoType.Way:
                rawChangeset.DeletedWays++;
                break;
            case OsmGeoType.Relation:
                rawChangeset.DeletedRelationships++;
                break;
        }
    }
    return changesets.Values.ToArray();
}

static string ReplicationFilePath(long sequenceNumber)
{
    string text = "000000000" + sequenceNumber;
    string text2 = text.Substring(text.Length - 9);
    string text3 = text2.Substring(0, 3);
    string text4 = text2.Substring(3, 3);
    string text5 = text2.Substring(6, 3);
    var filePath = $"{text3}/{text4}/{text5}.osc.gz";
    return filePath;
}

string DiffUrl(ReplicationConfig config, string filePath)
{
    return new Uri(new Uri(config.Url), filePath).ToString();
}

OsmChange DownloadChangeset(ReplicationConfig config, long sequenceNumber)
{
    bool ignoreCache = false;
    while (true)
    {
        try
        {
            var replicationFilePath = ReplicationFilePath(sequenceNumber);
            var url = DiffUrl(config, replicationFilePath);
            var cachePath = Path.Combine("ReplicationCache", config.IsDaily ? "daily" : config.IsHourly ? "hour" : "minute", replicationFilePath);
            if (ignoreCache || !File.Exists(cachePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                using FileStream fsw = File.Create(cachePath);
                using Stream stream = httpClient.GetStreamAsync(url).Result;
                stream.CopyTo(fsw);
            }
            using FileStream fs = File.OpenRead(cachePath);
            using GZipStream stream2 = new GZipStream(fs, CompressionMode.Decompress);
            using StreamReader textReader = new StreamReader(stream2);
            var changeset = ThreadLocalXmlSerializer.Value!.Deserialize(textReader) as OsmChange;
            if (changeset is null)
            {
                throw new InvalidOperationException("How we got replication state but no changeset?");
            }
            return changeset;
        }
        catch (Exception ex)
        {
            ignoreCache = true;
            Console.WriteLine("Failed to download/deserialize changeset: " + ex);
        }
    }
}

static RawChangeset GetRawChangeset(Dictionary<long, RawChangeset> changesets, OsmGeo item)
{
    if (!changesets.TryGetValue((long)item.ChangeSetId!, out var rawChangeset))
    {
        changesets[(long)item.ChangeSetId] = rawChangeset = new RawChangeset() {
            ChangesetId = (long)item.ChangeSetId,
            UserId = (long)item.UserId!,
            Username = item.UserName,
            Timestamp = (DateTime)item.TimeStamp!
        };
    }
    return rawChangeset;
}
class RawChangeset
{
    public long UserId { get; set; }
    public string Username { get; set; }
    public long ChangesetId { get; set; }
    public DateTime Timestamp { get; set; }

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


public class LimitsPerDay
{
    public const int CreatedNodesDefaultLimit = 3000;
    public const int ModifiedNodesDefaultLimit = 500;
    public const int DeletedNodesDefaultLimit = 500;
    public const int CreatedWaysDefaultLimit = 700;
    public const int ModifiedWaysDefaultLimit = 250;
    public const int DeletedWaysDefaultLimit = 200;
    public const int CreatedRelationshipsDefaultLimit = 100;
    public const int ModifiedRelationshipsDefaultLimit = 50;
    public const int DeletedRelationshipsDefaultLimit = 50;

    public int ChangesetsCountLimit { get; }
    public int CreatedNodesLimit { get; }
    public int ModifiedNodesLimit { get; }
    public int DeletedNodesLimit { get; }
    public int CreatedWaysLimit { get; }
    public int ModifiedWaysLimit { get; }
    public int DeletedWaysLimit { get; }
    public int CreatedRelationshipsLimit { get; }
    public int ModifiedRelationshipsLimit { get; }
    public int DeletedRelationshipsLimit { get; }

    public LimitsPerDay(DateTime accountCreatedDate, int changesetsCount, DateTime timestamp)
    {
        var accountAge = timestamp - accountCreatedDate;
        if (changesetsCount > 1000 && accountAge.TotalDays > 90)
        {
            ChangesetsCountLimit = int.MaxValue;
            CreatedNodesLimit = int.MaxValue;
            ModifiedNodesLimit = int.MaxValue;
            DeletedNodesLimit = int.MaxValue;
            CreatedWaysLimit = int.MaxValue;
            ModifiedWaysLimit = int.MaxValue;
            DeletedWaysLimit = int.MaxValue;
            CreatedRelationshipsLimit = int.MaxValue;
            ModifiedRelationshipsLimit = int.MaxValue;
            DeletedRelationshipsLimit = int.MaxValue;
            return;
        }

        ChangesetsCountLimit = 100;
        var increaseRation = 1.0;

        if (accountAge.TotalDays > 365)
        {
            increaseRation += 4.0;
        }
        else if (accountAge.TotalDays > 30)
        {
            increaseRation += 1;
        }
        else if (accountAge.TotalDays > 7)
        {
            increaseRation += 0.5;
        }

        if (changesetsCount > 500)
        {
            increaseRation += 1;
        }
        else if (changesetsCount > 100)
        {
            increaseRation += 0.5;
        }
        else if (changesetsCount > 50)
        {
            increaseRation += 0.3;
        }

        CreatedNodesLimit = (int)(CreatedNodesDefaultLimit * increaseRation);
        ModifiedNodesLimit = (int)(ModifiedNodesDefaultLimit * increaseRation);
        DeletedNodesLimit = (int)(DeletedNodesDefaultLimit * increaseRation);

        CreatedWaysLimit = (int)(CreatedWaysDefaultLimit * increaseRation);
        ModifiedWaysLimit = (int)(ModifiedWaysDefaultLimit * increaseRation);
        DeletedWaysLimit = (int)(DeletedWaysDefaultLimit * increaseRation);

        CreatedRelationshipsLimit = (int)(CreatedRelationshipsDefaultLimit * increaseRation);
        ModifiedRelationshipsLimit = (int)(ModifiedRelationshipsDefaultLimit * increaseRation);
        DeletedRelationshipsLimit = (int)(DeletedRelationshipsDefaultLimit * increaseRation);
    }
}
