namespace DBManager.Abstractions.Research;

public enum ResearchRunType
{
    WalkForward,
    Calibration,
    Ablation,
    Sensitivity,
    MonteCarlo
}

public enum ResearchRunStatus
{
    Requested,
    Running,
    Completed,
    Failed
}
