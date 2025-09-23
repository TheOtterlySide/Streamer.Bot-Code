namespace Streamer.Bot_Snippets;

public class ChangeRecordName
{
    public void Execute(Dictionary<string, object> args)
    {
        string game = args["twitch.game"]?.ToString();
    }
}