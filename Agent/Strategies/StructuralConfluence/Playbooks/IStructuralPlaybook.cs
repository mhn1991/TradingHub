using Agent.Strategies.StructuralConfluence.Evidence;

namespace Agent.Strategies.StructuralConfluence.Playbooks;

public interface IStructuralPlaybook
{
    string PlaybookId { get; }
    string Version { get; }

    PlaybookEvaluation Evaluate(
        StructuralEvidencePacket evidence,
        PlaybookRuntimeState state);
}
