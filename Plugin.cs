using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FateWalker.Controller;
using FateWalker.External;
using FateWalker.Windows;

namespace FateWalker;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fwalk";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IChatGui _chat;
    private readonly WindowSystem _windowSystem = new("FateWalker");
    private readonly MainWindow _mainWindow;
    private readonly FateController _controller;

    public Configuration Config { get; }
    public NavmeshIpc Navmesh { get; }
    public BossModIpc BossMod { get; }
    public LifestreamIpc Lifestream { get; }
    public TextAdvanceIpc TextAdvance { get; }
    public RsrIpc Rsr { get; }
    public YesAlreadyIpc YesAlready { get; }
    public FateController Controller => _controller;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IChatGui chat,
        IPluginLog log,
        IFateTable fateTable,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        ICondition condition,
        ITargetManager targetManager,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IDataManager dataManager,
        IKeyState keyState,
        IDutyState dutyState)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _chat = chat;

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Navmesh     = new NavmeshIpc(pluginInterface, log);
        BossMod     = new BossModIpc(pluginInterface, commandManager, log);
        Lifestream  = new LifestreamIpc(pluginInterface, log);
        TextAdvance = new TextAdvanceIpc(pluginInterface, log);
        Rsr         = new RsrIpc(pluginInterface, log);
        YesAlready  = new YesAlreadyIpc(pluginInterface, log);

        var selector = new FateSelector(Config);
        var action = new ActionExecutor(log);
        var fileLogger = new SessionFileLogger(pluginInterface);
        _controller = new FateController(
            Config, log, framework, clientState, condition,
            objectTable, fateTable, targetManager, Navmesh, BossMod, Rsr, Lifestream, TextAdvance, addonLifecycle, gameGui, chat, fileLogger, action, selector, dataManager, keyState, dutyState, YesAlready);
        _controller.SetSaveConfigCallback(SaveConfig);

        _mainWindow = new MainWindow(this, fateTable, clientState, objectTable);
        _windowSystem.AddWindow(_mainWindow);

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/fwalk [start|stop|toggle|status] — open the window, or control the bot."
        });

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += () => _mainWindow.IsOpen = true;
        _pluginInterface.UiBuilder.OpenConfigUi += () => _mainWindow.IsOpen = true;

        log.Information("FateWalker loaded. Type /fwalk to open.");
    }

    public void SaveConfig() => _pluginInterface.SavePluginConfig(Config);

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "":
            case "toggle":
                _mainWindow.IsOpen = !_mainWindow.IsOpen;
                break;
            case "start":
                _controller.Start();
                _chat.Print($"[FateWalker] start ({(Config.DryRun ? "DRY RUN" : "LIVE")}).");
                break;
            case "stop":
                _controller.Stop();
                _chat.Print("[FateWalker] stopped.");
                break;
            case "status":
                _chat.Print($"[FateWalker] state={_controller.State}, last={_controller.LastAction}");
                break;
            default:
                _chat.Print($"[FateWalker] unknown subcommand '{args}'. Try: start, stop, toggle, status.");
                break;
        }
    }

    public void Dispose()
    {
        _controller.Dispose();
        _commandManager.RemoveHandler(CommandName);
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _windowSystem.RemoveAllWindows();
        _mainWindow.Dispose();
    }
}
