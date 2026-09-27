using Microsoft.CodeAnalysis;
using ProtoBuf.BuildTools.Analyzers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace BuildToolsUnitTests
{
    // PBN0028's neighbours: the same question - what does construction leave in a member that the
    // payload does not carry - asked of a nullable member (PBN0029) and of a non-nullable one that
    // is not a collection (PBN0030). Every "reported" shape was probed against RuntimeTypeModel.
    public partial class ProtobufFieldAnalyzerTests
    {
        private const string NullMemberTypes = @"
public enum Color { Red, Green, Blue }
[ProtoContract] public class Msg { [ProtoMember(1)] public int X { get; set; } }";

        private async Task<List<Diagnostic>> DiagnosticsAsync(DiagnosticDescriptor descriptor, string source)
            => (await AnalyzeAsync(source)).Where(x => x.Descriptor == descriptor).ToList();

        private static string NullMemberSource(string contract, string body, string nullable = "#nullable enable", string kind = "class") => $@"
{nullable}
using ProtoBuf;
using System.Collections.Generic;
using System.ComponentModel;
{contract}
public {kind} Foo {{
    {body}
}}
" + NullMemberTypes;

        // PBN0029: null is not written, so the receiver keeps whatever the initializer gave it
        [Theory]
        [InlineData("[ProtoMember(1)] public int? V { get; set; } = 5;")]
        [InlineData("[ProtoMember(1)] public int? V = 5;")]
        [InlineData("[ProtoMember(1)] public bool? V { get; set; } = true;")]
        [InlineData("[ProtoMember(1)] public Color? V { get; set; } = Color.Blue;")]
        [InlineData("[ProtoMember(1)] public string? V { get; set; } = \"x\";")]
        [InlineData("[ProtoMember(1)] public Msg? V { get; set; } = new();")]
        [InlineData("[ProtoMember(1)] public List<int>? V { get; set; } = new();")]
        // neither changes what an absent field reads as
        [InlineData("[ProtoMember(1), NullWrappedCollection] public List<int>? V { get; set; } = new();")]
        [InlineData("[ProtoMember(1), DefaultValue(5)] public int? V { get; set; } = 5;")]
        [InlineData("[ProtoMember(1, IsRequired = true)] public int? V { get; set; } = 5;")]
        // a callback that resets something else does not count
        [InlineData(@"[ProtoMember(1)] public int? V { get; set; } = 5;
                      public int Other { get; set; }
                      [ProtoBeforeDeserialization] public void Reset() => Other = 0;")]
        public async Task ReportsNullableMemberWithInitializer(string body)
        {
            var diag = Assert.Single(await DiagnosticsAsync(DataContractAnalyzer.NullableMemberInitialized,
                NullMemberSource("[ProtoContract]", body)));
            Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
            Assert.StartsWith("'V' is nullable, but its initializer gives it a value; null is not written, so a null comes back as that value",
                diag.GetMessage(CultureInfo.InvariantCulture));

            var span = diag.Location.SourceSpan;
            Assert.Equal("V", (await diag.Location.SourceTree!.GetTextAsync()).ToString().Substring(span.Start, span.Length));
        }

        [Fact]
        public async Task ReportsNullableMemberWithInitializerInFull()
        {
            var diag = Assert.Single(await DiagnosticsAsync(DataContractAnalyzer.NullableMemberInitialized,
                NullMemberSource("[ProtoContract]", "[ProtoMember(1)] public int? Timeout { get; set; } = 30;")));
            Assert.Equal("'Timeout' is nullable, but its initializer gives it a value; null is not written, so a null comes back as that value. "
                + "To fix: remove the initializer, or reset it to null in a [ProtoBeforeDeserialization] callback.",
                diag.GetMessage(CultureInfo.InvariantCulture));
        }

        // Nullable<T> is nullable whatever the context: it needs no annotation to promise null
        [Fact]
        public async Task ReportsNullableValueTypeWithoutNullableContext()
        {
            Assert.Single(await DiagnosticsAsync(DataContractAnalyzer.NullableMemberInitialized,
                NullMemberSource("[ProtoContract]", "[ProtoMember(1)] public int? V { get; set; } = 5;", nullable: "#nullable disable")));
        }

        // SkipConstructor on an abstract type is inert: the concrete type runs the initializer
        [Fact]
        public async Task ReportsNullableMemberOnAbstractBase()
        {
            Assert.Single(await DiagnosticsAsync(DataContractAnalyzer.NullableMemberInitialized,
                NullMemberSource("[ProtoContract(SkipConstructor = true)]", "[ProtoMember(1)] public int? V { get; set; } = 5;", kind: "abstract class")));
        }

        // inside a [NullWrappedValue] wrapper the value is an ordinary field, so a zero or false is
        // left out and the wrapper goes out empty - which leaves the initializer's value in place.
        // An enum's zero is written explicitly, and a string is written even empty (both probed)
        [Theory]
        [InlineData("int? V { get; set; } = 5;", true)]
        [InlineData("bool? V { get; set; } = true;", true)]
        [InlineData("double? V { get; set; } = 1.5;", true)]
        [InlineData("Color? V { get; set; } = Color.Blue;", false)]
        [InlineData("string? V { get; set; } = \"x\";", false)]
        public async Task SaysWhenNullWrappingLosesZeroToo(string member, bool zeroLost)
        {
            var diag = Assert.Single(await DiagnosticsAsync(DataContractAnalyzer.NullableMemberInitialized,
                NullMemberSource("[ProtoContract]", "[ProtoMember(1), NullWrappedValue] public " + member)));
            var clause = ", as does a zero or false, which [NullWrappedValue] writes as an empty wrapper.";
            Assert.Equal(zeroLost, diag.GetMessage(CultureInfo.InvariantCulture).Contains(clause));
        }

        [Theory]
        // no initializer, or one that restates null
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public int? V { get; set; }")]
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public int? V { get; set; } = null;")]
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public int? V { get; set; } = default;")]
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public string? V { get; set; } = null!;")]
        // not nullable: losing a value there is PBN0020/PBN0022's business
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public int V { get; set; } = 5;")]
        // not written at all, so nothing is lost on the way
        [InlineData("[ProtoContract]", "public int? V { get; set; } = 5;")]
        // presence is managed explicitly, so the receiver can tell "absent" apart
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public int? V { get; set; } = 5; public bool VSpecified { get; set; }")]
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public int? V { get; set; } = 5; public bool ShouldSerializeV() => V != 5;")]
        // the documented remedy that keeps the initializer for instances built in code
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public int? V { get; set; } = 5; [ProtoBeforeDeserialization] public void Reset() => V = null;")]
        // no initializer runs on deserialize, so the null survives (probed)
        [InlineData("[ProtoContract(SkipConstructor = true)]", "[ProtoMember(1)] public int? V { get; set; } = 5;")]
        [InlineData("[ProtoContract(Surrogate = typeof(Msg))]", "[ProtoMember(1)] public int? V { get; set; } = 5;")]
        public async Task DoesNotReportNullableMember(string contract, string body)
        {
            Assert.Empty(await DiagnosticsAsync(DataContractAnalyzer.NullableMemberInitialized, NullMemberSource(contract, body)));
        }

        [Fact]
        public async Task DoesNotReportNullableMemberOnStructContract()
        {
            Assert.Empty(await DiagnosticsAsync(DataContractAnalyzer.NullableMemberInitialized,
                NullMemberSource("[ProtoContract]", "[ProtoMember(1)] public int? V = 5; public Foo() { }", kind: "struct")));
        }

        // IsRequired changes nothing on the wire for a Nullable<T> - it is written whenever it has a
        // value, zero included - and alongside [NullWrappedValue] protobuf-net refuses the model
        [Theory]
        [InlineData("[ProtoMember(1)] public int? V { get; set; } = 5;")]
        [InlineData("[ProtoMember(1), NullWrappedValue] public int? V { get; set; } = 5;")]
        [InlineData("[ProtoMember(1)] public System.DateTime? V { get; set; } = new System.DateTime(2000, 1, 1);")]
        public async Task DoesNotSuggestIsRequiredForNullableValueType(string body)
        {
            Assert.Empty(await DiagnosticsAsync(DataContractAnalyzer.ShouldDeclareIsRequired, NullMemberSource("[ProtoContract]", body)));
        }

        // PBN0030: a non-nullable member that is not a collection, where no initializer runs. A
        // serialized one is null whenever the payload lacks it - written before the member existed,
        // or by a sender that omits "" - and one that is not serialized, every time
        [Theory]
        [InlineData("[ProtoMember(1)] public string Value { get; set; } = \"\";")]
        [InlineData("[ProtoMember(1)] public required string Value { get; set; }")]
        [InlineData("[ProtoMember(1)] public Msg Value { get; set; } = new();")]
        [InlineData("[ProtoMember(1)] public byte[] Value { get; set; } = new byte[0];")]
        [InlineData("public readonly object Value = new();")]
        [InlineData("public string Value { get; } = \"x\";")]
        public async Task ReportsNonNullableMemberUnderSkipConstructor(string body)
        {
            var diag = Assert.Single(await DiagnosticsAsync(DataContractAnalyzer.NonNullableMemberLeftNull,
                NullMemberSource("[ProtoContract(SkipConstructor = true)]", body)));
            Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
            Assert.Equal("'Value' is non-nullable, but SkipConstructor means no constructor or initializer runs on deserialize; "
                + "it is null whenever the payload does not carry it, which for a member that is not serialized is every time. "
                + "To fix: declare it nullable, or restore it in a deserialization callback.",
                diag.GetMessage(CultureInfo.InvariantCulture));
        }

        [Fact]
        public async Task ReportsNonNullableMemberOnStructContract()
        {
            var diag = Assert.Single(await DiagnosticsAsync(DataContractAnalyzer.NonNullableMemberLeftNull,
                NullMemberSource("[ProtoContract]", "[ProtoMember(1)] public string Value;", kind: "struct")));
            Assert.StartsWith("'Value' is non-nullable, but a struct contract is never constructed on deserialize", diag.GetMessage(CultureInfo.InvariantCulture));
        }

        [Fact]
        public async Task ReportsNonNullablePositionalRecordMember()
        {
            var diag = Assert.Single(await DiagnosticsAsync(DataContractAnalyzer.NonNullableMemberLeftNull, @"
#nullable enable
using ProtoBuf;
[ProtoContract(SkipConstructor = true)]
public record Person([property: ProtoMember(1)] string Name);
" + IsExternalInit));
            var span = diag.Location.SourceSpan;
            Assert.Equal("Name", (await diag.Location.SourceTree!.GetTextAsync()).ToString().Substring(span.Start, span.Length));
        }

        [Theory]
        // constructed normally: only an absent field leaves it null, and CS8618 has had its say
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public string Value { get; set; } = null!;")]
        [InlineData("[ProtoContract]", "[ProtoMember(1)] public required string Value { get; set; }")]
        [InlineData("[ProtoContract(SkipConstructor = true)]", "[ProtoMember(1)] public string Value { get; set; } = \"\";", "#nullable enable", "abstract class")]
        // it has promised nothing, or cannot be null
        [InlineData("[ProtoContract(SkipConstructor = true)]", "[ProtoMember(1)] public string? Value { get; set; }")]
        [InlineData("[ProtoContract(SkipConstructor = true)]", "[ProtoMember(1)] public string Value { get; set; } = \"\";", "#nullable disable")]
        [InlineData("[ProtoContract(SkipConstructor = true)]", "[ProtoMember(1)] public int Value { get; set; } = 5;")]
        // restored where it matters
        [InlineData("[ProtoContract(SkipConstructor = true)]", @"[ProtoMember(1)] public string Value { get; set; } = """";
                      [ProtoAfterDeserialization] public void After() => Value ??= """";")]
        // a collection is PBN0028's
        [InlineData("[ProtoContract(SkipConstructor = true)]", "[ProtoMember(1)] public List<int> Value { get; set; } = new();")]
        [InlineData("[ProtoContract(SkipConstructor = true, Surrogate = typeof(Msg))]", "[ProtoMember(1)] public string Value { get; set; } = \"\";")]
        public async Task DoesNotReportNonNullableMember(string contract, string body, string nullable = "#nullable enable", string kind = "class")
        {
            Assert.Empty(await DiagnosticsAsync(DataContractAnalyzer.NonNullableMemberLeftNull, NullMemberSource(contract, body, nullable, kind)));
        }
    }
}
