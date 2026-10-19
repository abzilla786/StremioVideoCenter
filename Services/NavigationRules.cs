namespace StremioVideoCenter.Services
{
    // The screens of the Video Center's navigation hierarchy.
    //
    //   Profile Selector
    //         |
    //       Home
    //      /     \
    //   Movie    TV / Anime
    //     |         |
    //   Movie     Episode Selector
    //   Details       |
    //     |        Source Selector
    //   Source        |
    //   Selector    Player
    //     |
    //   Player
    public enum NavScreen
    {
        ProfileSelector,
        Home,
        EpisodeSelector,
        MovieDetails,
        SourceSelector,
        Player
    }

    public sealed class BackResult
    {
        // Where Back leads; ignored when ClosesApplication is true.
        public NavScreen Target { get; private set; }

        // True only for Back on the Profile Selector - the root of the
        // hierarchy and the ONLY Back that closes the application.
        public bool ClosesApplication { get; private set; }

        public BackResult(NavScreen target, bool closesApplication)
        {
            Target = target;
            ClosesApplication = closesApplication;
        }
    }

    // One central definition of what Back means on each screen. The Exit
    // button, controller B, Esc and Backspace all end in the same decision;
    // the view asks this class instead of re-stating the hierarchy.
    public static class NavigationRules
    {
        // isEpisodeContent: the item being browsed/played is a TV or anime
        // episode (reached through the Episode Selector) rather than a movie.
        public static BackResult Back(NavScreen current, bool isEpisodeContent)
        {
            switch (current)
            {
                case NavScreen.Player:
                    // Leaving the player is a deliberate exit: it returns to the
                    // Source Selector of the exact media being played.
                    return new BackResult(NavScreen.SourceSelector, false);

                case NavScreen.SourceSelector:
                    // A movie's Source Selector sits on top of its Movie
                    // Details page (Slice 19B); an episode's on top of the
                    // Episode Selector.
                    return new BackResult(
                        isEpisodeContent ? NavScreen.EpisodeSelector : NavScreen.MovieDetails,
                        false);

                case NavScreen.MovieDetails:
                    // Movie Details is a modal overlay over whichever screen
                    // opened it (Home, Search, My List, History...). Back
                    // simply uncovers that screen, so the target is "wherever
                    // the user came from" - Home is the hierarchy's parent.
                    return new BackResult(NavScreen.Home, false);

                case NavScreen.EpisodeSelector:
                    return new BackResult(NavScreen.Home, false);

                case NavScreen.Home:
                    return new BackResult(NavScreen.ProfileSelector, false);

                default:
                    return new BackResult(NavScreen.ProfileSelector, true);
            }
        }
    }
}
