using System;
using System.Text;

using Dalamud.Bindings.ImGui;

using Xunit;

namespace Dalamud.Test.Interface;

/// <summary>How ImGui splits labels into what is displayed and what is hashed for their IDs.</summary>
public unsafe class ImGuiLabelTests
{
    private const uint Seed = 0x12345678;

    [Theory]
    [InlineData("Save##toolbar", "Save", "Save##toolbar")]
    [InlineData("Title###WindowId", "Title", "WindowId")]
    [InlineData("Plain", "Plain", "Plain")]
    [InlineData("Label##", "Label", "Label##")]
    [InlineData("Label###", "Label", "")]
    [InlineData("A##B##", "A", "A##B##")]
    public void LabelsWithoutTheMarkerKeepTheirMeaning(string label, string text, string hashed)
    {
        Assert.Equal(text, Display(label));
        Assert.Equal(HashData(hashed), Hash(label));
    }

    [Theory]
    [InlineData("### Heading##intro\x1F##", "### Heading", "### Heading##intro")]
    [InlineData("C###id\x1F##", "C#", "C###id")]
    [InlineData("a####id\x1F##", "a##", "a####id")]
    [InlineData("x##id#\x1F##", "x", "x##id#")]
    [InlineData("a##b##c\x1F##", "a##b", "a##b##c")]
    [InlineData("### Heading\nBody##\x1F##", "### Heading\nBody", "### Heading\nBody##")]
    [InlineData("Body\x1F##", "Body", "Body")]
    [InlineData("\x1F##", "", "")]
    public void LabelsEndingWithTheMarkerAndTwoHashesAreHashedWithTheirText(
        string label, string text, string hashed)
    {
        Assert.Equal(text, Display(label));
        Assert.Equal(HashData(hashed), Hash(label));
    }

    [Theory]
    [InlineData("### Heading##intro\x1F###", "### Heading", "intro")]
    [InlineData("C###id\x1F###", "C#", "id")]
    [InlineData("x##i#d#\x1F###", "x", "i#d#")]
    [InlineData("a##b##c\x1F###", "a##b", "c")]
    [InlineData("id\x1F###", "", "id")]
    public void LabelsEndingWithTheMarkerAndThreeHashesAreHashedByTheirIdsOnly(
        string label, string text, string id)
    {
        Assert.Equal(text, Display(label));
        Assert.Equal(HashData(id), Hash(label));
    }

    [Theory]
    [InlineData("### Heading##intro\x1F##")]
    [InlineData("### Heading##intro\x1F###")]
    [InlineData("Body\x1F##")]
    [InlineData("Save##toolbar")]
    [InlineData("Title###WindowId")]
    public void ZeroTerminatedAndSizedLabelsHashTheSame(string label)
    {
        var bytes = Encoding.UTF8.GetBytes(label + "\0");
        fixed (byte* p = bytes)
        {
            var sized = ImGuiPNative.ImHashStr(p, (nuint)(bytes.Length - 1), Seed);
            Assert.Equal(sized, ImGuiPNative.ImHashStr(p, 0, Seed));
        }
    }

    [Theory]
    [InlineData("<colortype(500)>##<colortype(0)> text##id\x1F##")]
    [InlineData("a##b##id\x1F###")]
    [InlineData("a##b##\x1F##")]
    [InlineData("a##b##i#d#\x1F##")]
    public void DisplayedTextIsTheSameWhenItsEndIsFoundAgain(string label)
    {
        // As widgets do. They find the end, and then draw up to it, which finds the end again.
        var bytes = Encoding.UTF8.GetBytes(label + "\0");
        fixed (byte* p = bytes)
        {
            var end = ImGuiPNative.FindRenderedTextEnd(p, null);
            Assert.Equal((nint)end, (nint)ImGuiPNative.FindRenderedTextEnd(p, end));
        }
    }

    [Fact]
    public void IdsSavedFromTheirLabelsHashTheSame()
    {
        // .ini files keep the part of a window name from where its ID starts, and hash it again when loaded.
        Assert.Equal(Hash("Shown text##window\x1F###"), Hash("window\x1F###"));
    }

    [Theory]
    [InlineData("C#", "")]
    [InlineData("### Heading##x", "")]
    [InlineData("C#", "save")]
    [InlineData("### Heading##x", "i#d#")]
    [InlineData("C#", "##save")]
    [InlineData("C#", "###save")]
    public void LabelsWithIdsShowTheirTextAsItIs(string text, string id)
    {
        Assert.Equal(text, Display(new ImU8String(text, id).ToString()));
        Assert.Equal(text, Display(new ImU8String(text, id, false).ToString()));
    }

    [Fact]
    public void LabelsWithIdsHashTheirTextOnlyIfAsked()
    {
        Assert.Equal(HashData("save#"), LabelHash("A", "save#", false));
        Assert.Equal(HashData("A##save#"), LabelHash("A", "save#", true));
        Assert.Equal(HashData("A##B##"), LabelHash("A##B", "", true));
        Assert.Equal(HashData("A##B##"), LabelHash("A##B", "", false));

        // A prefix decides instead of appendId.
        Assert.Equal(HashData("save"), LabelHash("A", "###save", true));
        Assert.Equal(HashData("A##save"), LabelHash("A", "##save", false));

        static uint LabelHash(string text, string id, bool appendId) =>
            Hash(new ImU8String(text, id, appendId).ToString());
    }

    [Theory]
    [InlineData("#id")]
    [InlineData("i##d")]
    [InlineData("####id")]
    public void IdsThatCannotBeToldApartAreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => new ImU8String("text", id).ToString());

    [Fact]
    public void LongLabelsWithIdsAreRented()
    {
        var text = new string('x', ImU8String.AllocFreeBufferSize);
        Assert.Equal(text, Display(new ImU8String(text, "id").ToString()));
    }

    private static string Display(string label)
    {
        var bytes = Encoding.UTF8.GetBytes(label);
        ImGuiP.FindRenderedTextEnd(bytes, out var before, out _);
        return Encoding.UTF8.GetString(before);
    }

    private static uint Hash(string label)
    {
        var bytes = Encoding.UTF8.GetBytes(label + "\0");
        fixed (byte* p = bytes)
            return ImGuiPNative.ImHashStr(p, 0, Seed);
    }

    private static uint HashData(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        fixed (byte* p = bytes)
            return ImGuiPNative.ImHashData(p, (nuint)bytes.Length, Seed);
    }
}
