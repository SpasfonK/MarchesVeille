using MarchesVeille.Core.Config;
using MarchesVeille.Core.Data;
using MarchesVeille.Core.Ingestion;
using MarchesVeille.Core.Providers;

namespace MarchesVeille.Desktop;

/// <summary>Services partagés par les fenêtres.</summary>
public sealed class AppServices
{
    public AppServices(string dataDir, ConnectorsConfig cfg, Database db)
    {
        DataDir = dataDir;
        Config = cfg;
        Notices = new NoticeRepository(db);
        Archives = new ArchiveRepository(db);
        Registry = new ConnectorRegistry(cfg);
        Collector = new DceCollector(Archives, Registry.DceProviders, cfg.Dce, dataDir);
    }

    public string DataDir { get; }
    public ConnectorsConfig Config { get; }
    public NoticeRepository Notices { get; }
    public ArchiveRepository Archives { get; }
    public ConnectorRegistry Registry { get; }
    public DceCollector Collector { get; }
}
