using FSO.LotView;

namespace FSO.Client
{
    /// <summary>
    /// To avoid dynamically linking monogame from Program.cs (where we have to choose the correct version for the OS),
    /// we use this mediator class.
    /// </summary>
    public class GameStartProxy
    {
        public TSOGame Game;

        public void Start(bool useDX)
        {
            using var game = new TSOGame();
            Game = game;
            GameFacade.DirectX = useDX;
            World.DirectX = useDX;
            game.Run();
        }

        public void SetPath(string path)
        {
            GlobalSettings.Default.StartupPath = path;
            GlobalSettings.Default.Windowed = false;
        }
    }
}
