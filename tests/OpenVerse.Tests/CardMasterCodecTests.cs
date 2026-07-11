using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class CardMasterCodecTests
{
    static Dictionary<string, string> DecodeClientSide(string base64)
    {
        var gz = Convert.FromBase64String(base64);
        using var input = new MemoryStream(gz);
        using var decompress = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(decompress, Encoding.UTF8);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
    }

    [Fact]
    public void EncodeProducesBase64GzipJsonWithBothIds()
    {
        var encoded = CardMasterCodec.Encode("csv1", "csv2");
        var back = DecodeClientSide(encoded);
        Assert.Equal("csv1", back["1"]);
        Assert.Equal("csv2", back["2"]);
    }

    [Fact]
    public void NextCsvDefaultsToDefaultCsv()
    {
        var encoded = CardMasterCodec.Encode("only");
        var back = DecodeClientSide(encoded);
        Assert.Equal("only", back["1"]);
        Assert.Equal("only", back["2"]);
    }

    [Fact]
    public void ColumnsListMatchesClientSchema()
    {
        Assert.Equal(79, CardMasterCodec.Columns.Length);
        Assert.Equal("card_id", CardMasterCodec.Columns[0]);
        Assert.Equal("foil_card_id", CardMasterCodec.Columns[1]);
        Assert.Equal("CardNameId", CardMasterCodec.Columns[3]);
        Assert.Equal("char_type", CardMasterCodec.Columns[6]);
        Assert.Equal("clan", CardMasterCodec.Columns[7]);
        Assert.Equal("rarity", CardMasterCodec.Columns[16]);
        Assert.Equal("TwoPickFoilCardId", CardMasterCodec.Columns[77]);
        Assert.Equal("CardHashId", CardMasterCodec.Columns[78]);
    }

    [Fact]
    public void EncodedOutputIsCompressed()
    {
        var largeCsv = string.Join('\n', Enumerable.Range(0, 1000).Select(i => $"row {i} " + new string('x', 200)));
        var encoded = CardMasterCodec.Encode(largeCsv);
        var back = DecodeClientSide(encoded);
        Assert.Equal(largeCsv, back["1"]);
        var raw = Convert.FromBase64String(encoded);
        Assert.True(raw.Length < largeCsv.Length, "gzip should compress repetitive data");
    }
}
