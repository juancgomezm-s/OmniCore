using OmniCore.Domain;

using System.Text.Json;

namespace OmniCore.Tests;

/// <summary>
/// Tests de ModelQualificationKey (ADR-0007 §5): serialización canónica determinista,
/// hash SHA-256 estable, validación de campos obligatorios y de adapters.
/// </summary>
public sealed class ModelQualificationKeyTests
{
    private static ModelQualificationKey LocalKey(string quantization = "Q4_K_M") =>
        new("local", "qwen-x", "gguf-hash-abc123", quantization,
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-9", "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v3");

    // ---- Serialización canónica determinista ----

    [Fact]
    public void Same_key_produces_same_hash_and_canonical_json()
    {
        var a = LocalKey();
        var b = LocalKey();

        Assert.Equal(a.CanonicalJson(), b.CanonicalJson());
        Assert.Equal(a.QualificationKeyHash(), b.QualificationKeyHash());
        Assert.True(a.Equals(b));
    }

    [Fact]
    public void Canonical_json_is_valid_json_with_fixed_field_order()
    {
        var json = LocalKey().CanonicalJson();

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        // El primer campo es siempre providerId (orden fijo, no dependiente de cultura).
        Assert.Equal("local", root.GetProperty("providerId").GetString());
        Assert.Equal("qwen-x", root.GetProperty("modelId").GetString());
        Assert.Equal("prompt-profile-v3", root.GetProperty("promptProfileVersion").GetString());
        Assert.Equal(2, root.GetProperty("adapters").GetArrayLength());
        Assert.Equal("lora-a", root.GetProperty("adapters")[0].GetString());
    }

    [Fact]
    public void Canonical_json_escapes_tab_control_chars_quotes_and_unicode()
    {
        var key = new ModelQualificationKey("p", "m", null,
            "Q4_K_M\t" + "\u0008" + "\u000c" + "fin",
            new[] { "a\"b", "c" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");

        var json = key.CanonicalJson();
        var doc = JsonDocument.Parse(json);
        Assert.Equal("Q4_K_M\t" + "\u0008" + "\u000c" + "fin", doc.RootElement.GetProperty("quantization").GetString());
        Assert.Equal("a\"b", doc.RootElement.GetProperty("adapters")[0].GetString());
    }

    // ---- Cada campo relevante cambia el hash ----

    [Theory]
    [InlineData("rev-diferente", "Q4_K_M")]
    [InlineData("gguf-hash-abc123", "Q5_K_S")]
    public void Changing_model_revision_or_quantization_changes_hash(string revision, string quant)
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-x", revision, quant,
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-9", "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
        Assert.NotEqual(baseKey.CanonicalJson(), changed.CanonicalJson());
    }

    [Fact]
    public void Changing_backend_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-x", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "llama.cpp", "baac291", "tpl-9", "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    [Fact]
    public void Changing_backend_build_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-x", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "ik_llama", "otro-build", "tpl-9", "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    [Fact]
    public void Changing_chat_template_hash_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-x", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-otro", "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    [Fact]
    public void Changing_adapter_profile_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-x", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-9", "otro-profile",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    [Fact]
    public void Changing_tool_call_format_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-x", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-9", "default",
            ToolCallFormat.Native, ToolMode.Direct, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    [Fact]
    public void Changing_tool_mode_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-x", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-9", "default",
            ToolCallFormat.PromptedJson, ToolMode.Discovered, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    [Fact]
    public void Changing_prompt_profile_version_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-x", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-9", "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v4");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    [Fact]
    public void Changing_provider_id_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("otro-provider", "qwen-x", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-9", "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    [Fact]
    public void Changing_model_id_changes_hash()
    {
        var baseKey = LocalKey();
        var changed = new ModelQualificationKey("local", "qwen-otro", "gguf-hash-abc123", "Q4_K_M",
            new[] { "lora-a", "lora-b" }, "ik_llama", "baac291", "tpl-9", "default",
            ToolCallFormat.PromptedJson, ToolMode.Direct, "prompt-profile-v3");

        Assert.NotEqual(baseKey.QualificationKeyHash(), changed.QualificationKeyHash());
    }

    // ---- Separadores / unicode no colisionan ----

    [Fact]
    public void Entries_with_delimiters_and_unicode_do_not_collide()
    {
        // Dos valores que contienen el mismo separador JSON (coma) pero difieren en contenido.
        var a = new ModelQualificationKey("p", "m-a,b", null, null, Array.Empty<string>(),
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
        var b = new ModelQualificationKey("p", "m-aXb", null, null, Array.Empty<string>(),
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");

        Assert.NotEqual(a.QualificationKeyHash(), b.QualificationKeyHash());

        // Unicode: dos valores distintos que podrían colisionar bajo normalización cultural.
        var c = new ModelQualificationKey("p", "m", null, "cuánta", Array.Empty<string>(),
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
        var d = new ModelQualificationKey("p", "m", null, "cuanta", Array.Empty<string>(),
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");

        Assert.NotEqual(c.QualificationKeyHash(), d.QualificationKeyHash());
    }

    [Fact]
    public void Adapters_with_embedded_json_escapes_do_not_collide()
    {
        // Dos adapters con contenido distinto pero mismo largo: no colisionan.
        var a = new ModelQualificationKey("p", "m", null, null, new[] { "a\"b", "c" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
        var b = new ModelQualificationKey("p", "m", null, null, new[] { "aXb", "c" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");

        Assert.NotEqual(a.QualificationKeyHash(), b.QualificationKeyHash());
    }

    // ---- null vs vacío ----

    [Fact]
    public void Null_and_empty_string_differ_in_hash()
    {
        var withNull = new ModelQualificationKey("p", "m", null, null, Array.Empty<string>(),
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
        var withEmpty = new ModelQualificationKey("p", "m", "", null, Array.Empty<string>(),
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");

        Assert.NotEqual(withNull.QualificationKeyHash(), withEmpty.QualificationKeyHash());
        Assert.Contains("\"modelRevision\":null", withNull.CanonicalJson());
        Assert.Contains("\"modelRevision\":\"\"", withEmpty.CanonicalJson());
    }

    // ---- Adapters: orden y multiplicidad ----

    [Fact]
    public void Adapters_order_matters_in_qualification_key()
    {
        var a = new ModelQualificationKey("p", "m", null, null, new[] { "adapter-a", "adapter-b" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
        var b = new ModelQualificationKey("p", "m", null, null, new[] { "adapter-b", "adapter-a" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");

        Assert.NotEqual(a.CanonicalJson(), b.CanonicalJson());
        Assert.NotEqual(a.QualificationKeyHash(), b.QualificationKeyHash());
        Assert.False(a.Equals(b));
        // El orden canónico es el orden de entrada: se preserva.
        Assert.Equal(new[] { "adapter-a", "adapter-b" }, a.Adapters);
    }

    [Fact]
    public void Duplicate_adapters_are_preserved_in_qualification_key()
    {
        var a = new ModelQualificationKey("p", "m", null, null, new[] { "adapter-a", "adapter-a", "adapter-b" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
        var b = new ModelQualificationKey("p", "m", null, null, new[] { "adapter-a", "adapter-b" },
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");

        Assert.Equal(new[] { "adapter-a", "adapter-a", "adapter-b" }, a.Adapters);
        Assert.NotEqual(a.QualificationKeyHash(), b.QualificationKeyHash());
    }

    // ---- Adapters: inmutabilidad de la clave ----

    [Fact]
    public void Mutating_the_original_adapters_list_does_not_change_the_key()
    {
        var adapters = new List<string> { "adapter-a" };
        var key = new ModelQualificationKey("p", "m", null, null, adapters,
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");
        var jsonBefore = key.CanonicalJson();
        var hashBefore = key.QualificationKeyHash();

        adapters.Add("adapter-b");

        Assert.Equal(jsonBefore, key.CanonicalJson());
        Assert.Equal(hashBefore, key.QualificationKeyHash());
        Assert.Equal(new[] { "adapter-a" }, key.Adapters);
    }

    [Fact]
    public void Adapters_cast_does_not_allow_modifying_the_key()
    {
        var adapters = new List<string> { "adapter-a" };
        var key = new ModelQualificationKey("p", "m", null, null, adapters,
            null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1");

        var rawList = adapters;
        Assert.ThrowsAny<Exception>(() =>
        {
            typeof(System.Collections.ICollection).GetMethod("Add")!
                .Invoke(rawList, new object[] { "adapter-b" });
        });
    }

    [Fact]
    public void Rejects_null_adapters_list()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ModelQualificationKey("p", "m", null, null, null!, 
                null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1"));
    }

    [Fact]
    public void Rejects_null_adapter_entry()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModelQualificationKey("p", "m", null, null, new List<string> { "adapter-a", null! },
                null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1"));
    }

    [Fact]
    public void Rejects_empty_string_adapter_entry()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModelQualificationKey("p", "m", null, null, new[] { "", "adapter-a" },
                null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1"));
    }

    // ---- Hash SHA-256 ----

    [Fact]
    public void Hash_is_64_lower_case_hex_characters()
    {
        var hash = LocalKey().QualificationKeyHash();

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    // ---- Validación de campos obligatorios ----

    [Fact]
    public void Rejects_empty_provider_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModelQualificationKey("", "m", null, null, Array.Empty<string>(),
                null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1"));
    }

    [Fact]
    public void Rejects_empty_model_id()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModelQualificationKey("p", "", null, null, Array.Empty<string>(),
                null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, "v1"));
    }

    [Fact]
    public void Rejects_empty_adapter_profile()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModelQualificationKey("p", "m", null, null, Array.Empty<string>(),
                null, null, null, "", ToolCallFormat.Native, ToolMode.Direct, "v1"));
    }

    [Fact]
    public void Rejects_empty_prompt_profile_version()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModelQualificationKey("p", "m", null, null, Array.Empty<string>(),
                null, null, null, "default", ToolCallFormat.Native, ToolMode.Direct, ""));
    }

    // ---- For() helper ----

    [Fact]
    public void For_creates_minimal_local_key()
    {
        var key = ModelQualificationKey.For("local", "qwen-x", ToolCallFormat.PromptedJson, ToolMode.Direct);

        Assert.Equal("local", key.ProviderId);
        Assert.Equal("qwen-x", key.ModelId);
        Assert.Null(key.ModelRevision);
        Assert.Null(key.Quantization);
        Assert.Empty(key.Adapters);
        Assert.Null(key.Backend);
        Assert.Null(key.BackendBuild);
        Assert.Null(key.ChatTemplateHash);
        Assert.Equal("default", key.AdapterProfile);
        Assert.Equal(ToolCallFormat.PromptedJson, key.ToolCallFormat);
        Assert.Equal(ToolMode.Direct, key.ToolMode);
        Assert.Equal("v1", key.PromptProfileVersion);
        Assert.Matches("^[0-9a-f]{64}$", key.QualificationKeyHash());
    }
}
