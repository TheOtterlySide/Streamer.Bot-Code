namespace Streamer.Bot_Snippets;

public partial class CPH
{
    public static void SetRecordName(string name)
    {
        throw new NotImplementedException();
    }
    public static void TryGetArg(Dictionary<string, object> args, string key, out string value)
    {
        throw new NotImplementedException();
    }
}
public class ChangeRecordName
{
    public void Execute(Dictionary<string, object> args)
    {
        CPH.SetRecordName("New Recording Name");
        string game = args["twitch.game"]?.ToString();
    }
}