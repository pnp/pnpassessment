#nullable enable
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Pipeline.Contracts;

namespace PnP.Scanning.Core.Storage.Pipeline;

internal sealed class SourceSnapshotRow
{
    public Guid SnapshotId { get; set; }
    public Guid AssessmentId { get; set; }
    public string ModuleKey { get; set; } = null!;
    public string InputVersion { get; set; } = null!;
    public int FormatVersion { get; set; } = 1;
    public string ScopeJson { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsSealed { get; set; }
    public DateTime? SealedAtUtc { get; set; }
    public string? MemberIdsJson { get; set; }
    public string? ManifestJson { get; set; }
    public string? ManifestDigest { get; set; }
}

internal sealed class SourceObservationRow
{
    public Guid ObservationId { get; set; }
    public Guid SnapshotId { get; set; }
    public string SourceIdentity { get; set; } = null!;
    public string? SourceRevision { get; set; }
    public AcquisitionStatus AcquisitionStatus { get; set; }
    public string MetadataJson { get; set; } = null!;
    public string? AcquisitionError { get; set; }
    public Guid ArtifactId { get; set; }
}

internal sealed class SourceArtifactRow
{
    public Guid ArtifactId { get; set; }
    public Guid SnapshotId { get; set; }
    public Guid ObservationId { get; set; }
    public byte[]? RawBytes { get; set; }
    public long? Length { get; set; }
    public string? Sha256 { get; set; }
}

internal sealed class PhaseRunRow
{
    public Guid RunId { get; set; }
    public Guid AssessmentId { get; set; }
    public Guid? ParentRunId { get; set; }
    public PhaseKind Kind { get; set; }
    public PhaseKind CurrentPhase { get; set; }
    public ScanStatus Status { get; set; }
    public string ModuleKey { get; set; } = null!;
    public string InputVersion { get; set; } = null!;
    public Guid SnapshotId { get; set; }
    public Guid? AnalysisRunId { get; set; }
    public string? RuleVersion { get; set; }
    public string ParametersJson { get; set; } = null!;
    public string AnalysisParametersJson { get; set; } = null!;
    public string? CollectionOptionsJson { get; set; }
    public int Threads { get; set; }
    public int CompletedRecords { get; set; }
    public int TotalRecords { get; set; }
    public int ErrorCount { get; set; }
    public string? LastError { get; set; }
    public string ErrorsJson { get; set; } = "{\"schemaVersion\":1,\"value\":[]}";
    public string? CheckpointJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
}

internal sealed class AnalysisRunRow
{
    public Guid AnalysisRunId { get; set; }
    public Guid SnapshotId { get; set; }
    public string ModuleKey { get; set; } = null!;
    public string RuleVersion { get; set; } = null!;
    public string ParametersJson { get; set; } = null!;
    public string ManifestDigest { get; set; } = null!;
    public DateTime CreatedAtUtc { get; set; }
}

internal sealed class AnalysisResultRow
{
    public Guid AnalysisRunId { get; set; }
    public Guid ObservationId { get; set; }
    public Guid SnapshotId { get; set; }
    public AnalysisOutcome Outcome { get; set; }
    public string Reason { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;
    public DateTime CommittedAtUtc { get; set; }
}

internal static class PipelineModel
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SourceSnapshotRow>(e =>
        {
            e.ToTable("SourceSnapshots");
            e.HasKey(x => x.SnapshotId);
            e.HasAlternateKey(x => new { x.SnapshotId, x.AssessmentId });
            e.HasOne<Scan>().WithMany().HasForeignKey(x => x.AssessmentId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SourceObservationRow>(e =>
        {
            e.ToTable("SourceObservations");
            e.HasKey(x => x.ObservationId);
            e.HasAlternateKey(x => new { x.ObservationId, x.SnapshotId });
            e.HasIndex(x => new { x.SnapshotId, x.SourceIdentity }).IsUnique();
            e.Property(x => x.AcquisitionStatus).HasConversion<string>();
            e.HasOne<SourceSnapshotRow>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<SourceArtifactRow>(e =>
        {
            e.ToTable("SourceArtifacts");
            e.HasKey(x => x.ArtifactId);
            e.HasIndex(x => new { x.ObservationId, x.SnapshotId }).IsUnique();
            e.HasOne<SourceObservationRow>().WithMany().HasForeignKey(x => new { x.ObservationId, x.SnapshotId })
                .HasPrincipalKey(x => new { x.ObservationId, x.SnapshotId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<PhaseRunRow>(e =>
        {
            e.ToTable("PhaseRuns");
            e.HasKey(x => x.RunId);
            e.HasAlternateKey(x => new { x.RunId, x.SnapshotId });
            e.HasIndex(x => new { x.AssessmentId, x.ParentRunId, x.CreatedAtUtc });
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.CurrentPhase).HasConversion<string>();
            e.HasOne<SourceSnapshotRow>().WithMany().HasForeignKey(x => new { x.SnapshotId, x.AssessmentId })
                .HasPrincipalKey(x => new { x.SnapshotId, x.AssessmentId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<PhaseRunRow>().WithMany().HasForeignKey(x => x.ParentRunId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AnalysisRunRow>(e =>
        {
            e.ToTable("AnalysisRuns");
            e.HasKey(x => x.AnalysisRunId);
            e.HasAlternateKey(x => new { x.AnalysisRunId, x.SnapshotId });
            e.HasOne<PhaseRunRow>().WithOne().HasForeignKey<AnalysisRunRow>(x => new { x.AnalysisRunId, x.SnapshotId })
                .HasPrincipalKey<PhaseRunRow>(x => new { x.RunId, x.SnapshotId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<AnalysisResultRow>(e =>
        {
            e.ToTable("AnalysisResults");
            e.HasKey(x => new { x.AnalysisRunId, x.ObservationId });
            e.Property(x => x.Outcome).HasConversion<string>();
            e.HasOne<AnalysisRunRow>().WithMany().HasForeignKey(x => new { x.AnalysisRunId, x.SnapshotId })
                .HasPrincipalKey(x => new { x.AnalysisRunId, x.SnapshotId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<SourceObservationRow>().WithMany().HasForeignKey(x => new { x.ObservationId, x.SnapshotId })
                .HasPrincipalKey(x => new { x.ObservationId, x.SnapshotId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
