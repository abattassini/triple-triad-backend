using TripleTriadApi.Models;
using TripleTriadApi.Repositories;

namespace TripleTriadApi.Services
{
    /// <summary>
    /// Applies the post-match economy (coins, experience and win/loss/tie counters)
    /// to both players once a match is completed.
    /// </summary>
    public class MatchRewardService(IPlayerRepository playerRepository)
    {
        private readonly IPlayerRepository _playerRepository = playerRepository;

        // Reward table (tunable in one place).
        public const int WinCoins = 200;
        public const int TieCoins = 80;
        public const int LossCoins = 20;
        public const int WinExperience = 50;
        public const int TieExperience = 15;
        public const int LossExperience = 0;

        /// <summary>Rewards granted to each side of a completed match.</summary>
        public class MatchRewardResult
        {
            public int Player1Coins { get; set; }
            public int Player1Experience { get; set; }
            public int Player2Coins { get; set; }
            public int Player2Experience { get; set; }
        }

        /// <summary>
        /// Awards both players of a completed match. Missing logins (e.g. the "AI"
        /// opponent) are skipped so the match can still complete cleanly.
        /// </summary>
        public async Task<MatchRewardResult> AwardForMatchAsync(Match match)
        {
            var (p1Coins, p1Xp, p1Win, p1Loss, p1Tie) = ResolveOutcome(match, match.Player1Id);
            var (p2Coins, p2Xp, p2Win, p2Loss, p2Tie) = ResolveOutcome(match, match.Player2Id);

            await ApplyAsync(match.Player1Id, p1Coins, p1Xp, p1Win, p1Loss, p1Tie);
            await ApplyAsync(match.Player2Id, p2Coins, p2Xp, p2Win, p2Loss, p2Tie);

            return new MatchRewardResult
            {
                Player1Coins = p1Coins,
                Player1Experience = p1Xp,
                Player2Coins = p2Coins,
                Player2Experience = p2Xp,
            };
        }

        /// <summary>Determines the coins/XP and which counter to bump for one player.</summary>
        private static (int coins, int xp, bool win, bool loss, bool tie) ResolveOutcome(
            Match match,
            string playerId
        )
        {
            if (!string.IsNullOrEmpty(match.WinnerId) && match.WinnerId == playerId)
            {
                return (WinCoins, WinExperience, true, false, false);
            }

            // No winner recorded -> draw.
            if (string.IsNullOrEmpty(match.WinnerId))
            {
                return (TieCoins, TieExperience, false, false, true);
            }

            return (LossCoins, LossExperience, false, true, false);
        }

        private async Task ApplyAsync(
            string login,
            int coins,
            int experience,
            bool win,
            bool loss,
            bool tie
        )
        {
            if (string.IsNullOrEmpty(login) || login == "AI")
            {
                return;
            }

            var player = await _playerRepository.FindByLoginAsync(login);
            if (player is null)
            {
                return;
            }

            player.Coins += coins;
            player.Experience += experience;
            if (win)
            {
                player.Wins++;
            }
            if (loss)
            {
                player.Losses++;
            }
            if (tie)
            {
                player.Ties++;
            }

            await _playerRepository.UpdateAsync(player);
        }
    }
}
