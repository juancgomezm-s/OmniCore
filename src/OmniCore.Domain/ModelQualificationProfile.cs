namespace OmniCore.Domain;

/// <summary>
/// Estados de cualificación (ADR-0007 §4). La máquina de estados vive en arquitectura.md §13:
/// Unknown → Declared → ProvisionallyClassified → Qualified → Calibrated, y Qualified/Calibrated
/// pueden pasar a Stale ante una versión mayor nueva de la suite.
/// </summary>
public enum ModelQualificationState
{
    Unknown,
    Declared,
    ProvisionallyClassified,
    Qualified,
    Calibrated,
    Stale,
}

/// <summary>
/// Perfil de cualificación empírica persistido en el almacén de scope User (ADR-0007 §6).
/// Tabla `model_profiles`: key_hash, key_json, state, profile_revision, suite_id, suite_version,
/// created_at. La cualificación es de la máquina y el modelo, no del workspace.
/// </summary>
public sealed class ModelQualificationProfile
{
    public ModelQualificationKey Key { get; }

    public string KeyHash { get; }

    public ModelQualificationState State { get; }

    public long ProfileRevision { get; }

    public string SuiteId { get; }

    /// <summary>
    /// Versión de la suite que PRODUJO esta cualificación. `MarkStale` nunca la sobrescribe:
    /// la nueva versión de la suite queda en <see cref="StaleBySuiteVersion"/>.
    /// </summary>
    public string SuiteVersion { get; }

    /// <summary>
    /// Versión de la suite (misma suite) que marcó este perfil Stale; null si el perfil no está
    /// Stale. Solo se registra cuando el perfil proviene de esa suite (versión mayor superior).
    /// </summary>
    public string? StaleBySuiteVersion { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public ModelQualificationProfile(ModelQualificationKey key, ModelQualificationState state,
        long profileRevision, string suiteId, string suiteVersion, DateTimeOffset createdAt,
        DateTimeOffset updatedAt) : this(key, state, profileRevision, suiteId, suiteVersion,
        null, createdAt, updatedAt)
    {
    }

    public ModelQualificationProfile(ModelQualificationKey key, ModelQualificationState state,
        long profileRevision, string suiteId, string suiteVersion, string? staleBySuiteVersion,
        DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Key = key;
        KeyHash = key.QualificationKeyHash();
        State = state;
        ProfileRevision = profileRevision;
        SuiteId = suiteId;
        SuiteVersion = suiteVersion;
        StaleBySuiteVersion = staleBySuiteVersion;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }
}

/// <summary>
/// Trait empírico de un perfil cualificado (ADR-0007 §2, §6). Tabla `model_traits`:
/// key_hash, profile_revision, trait, value, confidence, samples, source.
/// </summary>
public sealed class ModelTraitRecord
{
    public string KeyHash { get; }

    public long ProfileRevision { get; }

    public string Trait { get; }

    public double Value { get; }

    public double Confidence { get; }

    public int Samples { get; }

    public string Source { get; }

    public ModelTraitRecord(string keyHash, long profileRevision, string trait, double value,
        double confidence, int samples, string source)
    {
        if (value < 0.0 || value > 1.0 || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "ModelTraitRecord.Value debe estar en 0..1 y ser finito");
        }

        if (confidence < 0.0 || confidence > 1.0 || !double.IsFinite(confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence), "ModelTraitRecord.Confidence debe estar en 0..1 y ser finito");
        }

        if (samples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "ModelTraitRecord.Samples no puede ser negativo");
        }

        KeyHash = keyHash;
        ProfileRevision = profileRevision;
        Trait = trait;
        Value = value;
        Confidence = confidence;
        Samples = samples;
        Source = source;
    }
}

/// <summary>
/// Identidad del benchmark que produjo una cualificación (ADR-0007 §6). suite_id, suite_version,
/// hash del conjunto de tareas, semilla, temperatura y versión de OmniCore. Permite decidir si un
/// perfil quedó Stale ante una versión mayor nueva de la suite.
/// </summary>
public sealed class BenchmarkIdentity
{
    public string SuiteId { get; }

    public string SuiteVersion { get; }

    public string TaskSetHash { get; }

    public int Seed { get; }

    public double Temperature { get; }

    public string OmniCoreVersion { get; }

    public BenchmarkIdentity(string suiteId, string suiteVersion, string taskSetHash, int seed,
        double temperature, string omniCoreVersion)
    {
        SuiteId = suiteId;
        SuiteVersion = suiteVersion;
        TaskSetHash = taskSetHash;
        Seed = seed;
        Temperature = temperature;
        OmniCoreVersion = omniCoreVersion;
    }
}

/// <summary>
/// Conflicto de revisión de perfil de cualificación: dos clientes no pueden pisarse
/// (equivalente de ADR-0044 §9 para model_profiles). Sin sobrescritura silenciosa.
/// </summary>
public sealed class ModelQualificationRevisionConflictException : Exception
{
    public long ExpectedRevision { get; }

    public long ActualRevision { get; }

    public ModelQualificationRevisionConflictException(long expectedRevision, long actualRevision)
        : base("revisión de perfil de cualificación obsoleta: esperaba " + expectedRevision
            + ", actual " + actualRevision)
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }
}

/// <summary>
/// `MarkStale` no aplica: el estado no es Qualified/Calibrated, o la versión nueva de la suite no
/// es una versión mayor de la misma suite que produjo la cualificación.
/// </summary>
public sealed class ModelQualificationStaleException : Exception
{
    public ModelQualificationStaleException(string reason)
        : base(reason)
    {
    }
}
