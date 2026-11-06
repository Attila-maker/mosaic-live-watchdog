using System.Text;
using System.Text.Json;

namespace NodeWatch.Tests;

/// <summary>Vectors from Python's xxhash and hashlib.blake2b and from well-known Substrate values.</summary>
internal static class SubstrateTests
{
    private static readonly byte[] Data = [.. Enumerable.Range(0, 512).Select(i => (byte)(i % 256))];

    [Test]
    public static void XxHash64_matches_reference()
    {
        (int Len, string Seed0, string Seed1)[] vectors =
        [
            (0, "ef46db3751d8e999", "d5afba1336a3be4b"), (1, "e934a84adb052768", "771917c7f6ee2451"),
            (3, "e5c7bb4533bc65dd", "a2168d89c582b451"), (4, "ffced8604453cc1e", "94506f8c7e5870a9"),
            (7, "14cc643f630c72d2", "af4c5311c47c77b7"), (8, "884a173614b81b8d", "9d2b7c7354fe4e23"),
            (31, "c346d2b59b4d8ee1", "f031031d65977dfc"), (32, "cbf59c5116ff32b4", "d74e6766ce9dba94"),
            (33, "0c535d1acafb8ead", "a371825f4210fe99"), (63, "e26aa9e2a95f8e4f", "5264ec0719e10595"),
            (64, "f7c67301db6713f0", "3ce5bdf7575926c0"), (100, "6ac1e58032166597", "3d19a3a2098a7023"),
            (200, "50dc1079b99e879c", "20bd094800cb1dfa"), (511, "828118695502a203", "66f56b1f7b9e0b09"),
        ];
        foreach (var (len, s0, s1) in vectors)
        {
            Assert.Equal(s0, XxHash64.Hash(Data.AsSpan(0, len), 0).ToString("x16"), $"xxh64 len={len} seed=0");
            Assert.Equal(s1, XxHash64.Hash(Data.AsSpan(0, len), 1).ToString("x16"), $"xxh64 len={len} seed=1");
        }
        Assert.Equal("fbcea83c8a378bf1",
            XxHash64.Hash(Encoding.ASCII.GetBytes("Nobody inspects the spammish repetition"), 0).ToString("x16"), "xxh64 text");
    }

    [Test]
    public static void Twox128_matches_known_storage_prefixes()
    {
        Assert.Equal("26aa394eea5630e07c48ae0c9558cef7", Convert.ToHexStringLower(StorageKeys.Twox128("System")), "System");
        Assert.Equal("b99d880ec681799c0cf30e8886371da9", Convert.ToHexStringLower(StorageKeys.Twox128("Account")), "Account");
    }

    [Test]
    public static void Blake2b_matches_reference()
    {
        Assert.Equal("ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            Convert.ToHexStringLower(Blake2b.Hash("abc"u8)), "abc");
        (int Len, string Hex)[] vectors =
        [
            (0, "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce"),
            (1, "2fa3f686df876995167e7c2e5d74c4c7b6e48f8068fe0e44208344d480f7904c36963e44115fe3eb2a3ac8694c28bcb4f5a0f3276f2e79487d8219057a506e4b"),
            (127, "b6292669ccd38d5f01caae96ba272c76a879a45743afa0725d83b9ebb26665b731f1848c52f11972b6644f554c064fa90780dbbbf3a89d4fc31f67df3e5857ef"),
            (128, "2319e3789c47e2daa5fe807f61bec2a1a6537fa03f19ff32e87eecbfd64b7e0e8ccff439ac333b040f19b0c4ddd11a61e24ac1fe0f10a039806c5dcc0da3d115"),
            (129, "f59711d44a031d5f97a9413c065d1e614c417ede998590325f49bad2fd444d3e4418be19aec4e11449ac1a57207898bc57d76a1bcf3566292c20c683a5c4648f"),
            (255, "5b21c5fd8868367612474fa2e70e9cfa2201ffeee8fafab5797ad58fefa17c9b5b107da4a3db6320baaf2c8617d5a51df914ae88da3867c2d41f0cc14fa67928"),
            (256, "1ecc896f34d3f9cac484c73f75f6a5fb58ee6784be41b35f46067b9c65c63a6794d3d744112c653f73dd7deb6666204c5a9bfa5b46081fc10fdbe7884fa5cbf8"),
            (300, "d9cf5983dc6b34c0fa1f0226926855ad3eccd2bcdcd8f8053b9a80664d33b5afcc32fd21c70ea14f4ef50ca97c3203c4d1803159f0e01bb6cb1d1c83db52b63c"),
        ];
        foreach (var (len, hex) in vectors)
            Assert.Equal(hex, Convert.ToHexStringLower(Blake2b.Hash(Data.AsSpan(0, len))), $"blake2b len={len}");
    }

    private const string AlicePub = "d43593c715fdd31c61141abd04a99fd6822c8558854ccde39a5684e7a56da27d";

    [Test]
    public static void Ss58_decodes_generic_and_polkadot_addresses()
    {
        Assert.Equal(AlicePub, Convert.ToHexStringLower(Ss58.DecodePublicKey("5GrwvaEF5zXb26Fz9rcQpDWS57CtERHpNehXCPcNoHGKutQY")), "prefix 42");
        Assert.Equal(AlicePub, Convert.ToHexStringLower(Ss58.DecodePublicKey("15oF4uVJwmo4TdGW7VfQxNLavjCXviqxT9S1MgbjMNHr6Sp5")), "prefix 0");
        Assert.Equal(AlicePub, Convert.ToHexStringLower(Ss58.DecodePublicKey("0x" + AlicePub)), "hex");
    }

    [Test]
    public static void Ss58_rejects_typos()
    {
        Assert.Throws<FormatException>(() => Ss58.DecodePublicKey("15oF4uVJwmo4TdGW7VfQxNLavjCXviqxT9S1MgbjMNHr6Sp6"), "checksum");
        Assert.Throws<FormatException>(() => Ss58.DecodePublicKey("15oF4uVJwmo4TdGW7VfQxNLavjCXviqxT9S1MgbjMNHr6Sp0"), "invalid char");
        Assert.Throws<FormatException>(() => Ss58.DecodePublicKey("0x1234"), "short hex");
    }

    [Test]
    public static void ValidatorStates_key_matches_python()
    {
        Assert.Equal("0x7ceaa2e511e6cb711de16e0834dc2abd167d3abe532add5d84571108d9ccd3e3518366b5b1bc7c99" + AlicePub,
            StorageKeys.ValidatorState(Convert.FromHexString(AlicePub)), "storage key");
    }

    [Test]
    public static void ValidatorState_decodes_scale_enum()
    {
        Assert.Equal(ValidatorStateKind.Normal, ValidatorState.Decode([0]).Kind, "normal");
        Assert.Equal(ValidatorStateKind.Faulted, ValidatorState.Decode([1]).Kind, "faulted");
        var chilled = ValidatorState.Decode([2, 0x10, 0x27, 0, 0]);
        Assert.Equal(ValidatorStateKind.Chilled, chilled.Kind, "chilled");
        Assert.Equal(10000u, chilled.ChilledSession, "session");
        Assert.Throws<FormatException>(() => ValidatorState.Decode([3]), "unknown");
    }

    [Test]
    public static void Peers_are_counted_by_role()
    {
        var json = JsonDocument.Parse("""
            [{"roles":"NONE"},{"roles":"NONE"},{"roles":"AUTHORITY"},{"roles":"FULL"},{"roles":"LIGHT"},{"roles":"FULL | AUTHORITY"}]
            """).RootElement;
        var p = NodeProbe.CountPeers(json);
        Assert.Equal(6, p.Total, "total");
        Assert.Equal(2, p.None, "none");
        Assert.Equal(1, p.Full, "full");
        Assert.Equal(2, p.Authority, "authority");
        Assert.Equal(3, p.SyncSources, "sync sources");
    }
}
