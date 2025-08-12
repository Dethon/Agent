using Domain.DTOs;

namespace Domain.Tools;

public class ContentRecommendationTool
{
    protected const string Name = "ContentRemmendationTool";

    protected const string Description = """
                                         Given a user prompt, this tool generates a set of recommendations for 
                                         downloadable content that satisifies the user's requierements.
                                         This tool has the caability of remembering previous context, so only the last 
                                         user prompt needs to be sent as a parameter. 
                                         For subsequent calls it is important to include the user's remarks, if any.
                                         """;

    protected const string SystemPrompt = """
                                          You are a content recommendation tool that provides suggestions based on user
                                          prompts.
                                          Your recommendations must be of content that can typically be downloaded, 
                                          including but not limited to books, audiobooks, movies, series, anime, music, 
                                          magazines, video-games....
                                          The format of the recommendations must be a list of items that includes 
                                          release year, title, and a brief description.
                                          Include at least 5 recommendations.
                                          """;

    protected static AiMessage[] GetFullPrompt(string userPrompt)
    {
        return
        [
            new AiMessage
            {
                Role = AiMessageRole.User,
                Content = userPrompt
            }
        ];
    }
}