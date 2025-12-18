using Domain.DTOs;

namespace Domain.Tools;

public class ContentRecommendationTool
{
    protected const string Name = "ContentRecommendationTool";

    protected const string Description = """
                                         Generates personalized content recommendations (movies, series, books, music, games, etc.) 
                                         based on user preferences, mood, or specific criteria.

                                         WHEN TO USE: Call this tool when the user asks for suggestions, recommendations, or ideas 
                                         about what to watch, read, listen to, or play. Also use when user describes a mood, genre, 
                                         or theme they're interested in exploring.

                                         QUERY PARAMETER: Pass the user's complete request as the query. Include:
                                         - Content type if specified (movie, series, book, anime, music, game)
                                         - Genre, mood, or theme preferences
                                         - Any specific criteria (era, language, similar to X, etc.)
                                         - User feedback from previous recommendations if refining results

                                         EXAMPLES:
                                         - "sci-fi movies similar to Blade Runner"
                                         - "relaxing jazz albums for working"
                                         - "fantasy book series with strong female protagonists"
                                         - "horror anime from the 90s"
                                         """;

    protected const string SystemPrompt = """
                                          You are a content recommendation tool that provides suggestions based on user
                                          prompts.
                                          Your recommendations must be of content types including but not limited to 
                                          books, audiobooks, movies, series, anime, music, magazines, video-games....
                                          The format of the recommendations must be a list of items that includes 
                                          release year, title, and a brief description.
                                          Include at least 5 recommendations. 
                                          The recommendations must be generated in one shot, do not ask the user for 
                                          more information. If the user wants to refine the recommendations, they will 
                                          provide additional context in the subsequent prompt.
                                          The output must JUST be recommendations, do not mention downloads, resolution,
                                          etc. as you are not a download tool.
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