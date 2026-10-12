using System.Text;

namespace KickFlight.BootstrapApi;

/// <summary>Builds the versioned iOS Octo database from its separately pinned native catalog.</summary>
public static class IosOctoDatabase
{
    public static byte[] Build(ResourceCatalogSnapshot snapshot, int fromRevision)
    {
        if (snapshot.Platform != ResourceCatalogStore.IosAssetPlatform ||
            snapshot.AssetVersion != ResourceCatalogStore.IosAssetVersion ||
            snapshot.Revision != ResourceCatalogStore.IosCatalogRevision ||
            snapshot.Resources.Count != ResourceCatalogStore.IosExpectedResourceCount || snapshot.Errors.Count != 0)
            throw new InvalidDataException("The iOS Octo database requires the complete pinned native catalog.");
        if (fromRevision is < 0 or > ResourceCatalogStore.IosWireRevision)
            throw new ArgumentOutOfRangeException(nameof(fromRevision));

        var output = new List<byte>();
        WriteVarintField(output, 1, ResourceCatalogStore.IosWireRevision);
        if (fromRevision < ResourceCatalogStore.IosWireRevision)
        {
            var state = fromRevision == 0 ? 1 : 2;
            var rows = snapshot.Resources.OrderBy(resource => resource.Definition.OctoId).ToArray();
            foreach (var resource in rows.Where(resource => resource.Definition.Kind == "assetBundle"))
                WriteBytesField(output, 2, EncodeData(resource.Definition, state));
            WriteStringField(output, 3, "common");
            foreach (var resource in rows.Where(resource => resource.Definition.Kind == "resource"))
                WriteBytesField(output, 4, EncodeData(resource.Definition, state));
        }
        WriteStringField(output, 5, "https://kick-flight-fenix.us.ci/ios/cdn/{o}");
        return output.ToArray();
    }

    private static byte[] EncodeData(ResourceDefinition resource, int state)
    {
        var id = resource.OctoId ?? throw new InvalidDataException($"iOS catalog row {resource.Id} has no stable Octo wire ID.");
        var name = resource.OctoName;
        if (name is null && resource.Id == "ios-octo-R-12Cxc3" &&
            resource.Kind == "resource" && resource.LogicalName == "resource/12Cxc3")
            name = resource.LogicalName;
        var objectName = resource.RequestPath[(resource.RequestPath.LastIndexOf('/') + 1)..];
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(objectName) ||
            resource.Bytes is null || resource.Md5 is null || resource.Crc32 is null)
            throw new InvalidDataException($"iOS catalog row {resource.Id} is missing Octo wire metadata.");

        var data = new List<byte>();
        WriteVarintField(data, 1, checked((ulong)id));
        WriteStringField(data, 2, name);
        WriteStringField(data, 3, name);
        WriteVarintField(data, 4, checked((ulong)resource.Bytes.Value));
        WriteVarintField(data, 5, resource.Crc32.Value);
        WriteVarintField(data, 7, 1);
        WriteVarintField(data, 9, checked((ulong)state));
        WriteStringField(data, 10, resource.Md5);
        WriteStringField(data, 11, objectName);
        WriteVarintField(data, 12, 1);
        WriteVarintField(data, 13, 1);
        return data.ToArray();
    }

    private static void WriteVarintField(List<byte> output, int field, ulong value)
    {
        WriteVarint(output, checked((ulong)field << 3));
        WriteVarint(output, value);
    }

    private static void WriteStringField(List<byte> output, int field, string value) =>
        WriteBytesField(output, field, Encoding.UTF8.GetBytes(value));

    private static void WriteBytesField(List<byte> output, int field, byte[] value)
    {
        WriteVarint(output, (checked((ulong)field) << 3) | 2);
        WriteVarint(output, checked((ulong)value.Length));
        output.AddRange(value);
    }

    private static void WriteVarint(List<byte> output, ulong value)
    {
        while (value >= 0x80)
        {
            output.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        output.Add((byte)value);
    }
}
