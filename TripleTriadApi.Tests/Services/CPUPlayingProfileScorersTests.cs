using TripleTriadApi.Models;
using TripleTriadApi.Services;

namespace TripleTriadApi.Tests.Services
{
    /// <summary>
    /// The scoring a profile picks (plans/PLAN-029-cpu-playing-profiles/plan.md): one <see cref="MoveScorer"/> per
    /// <see cref="CPUPlayingProfile"/>, chosen by <see cref="CPUPlayingProfileScorers"/>, whose fallback is Decent.
    /// </summary>
    public class CPUPlayingProfileScorersTests
    {
        [Fact]
        public void For_ReturnsTheScorerTheProfileNames()
        {
            Assert.IsType<DecentMoveScorer>(CPUPlayingProfileScorers.For(CPUPlayingProfile.Decent));
            Assert.IsType<FormidableMoveScorer>(
                CPUPlayingProfileScorers.For(CPUPlayingProfile.Formidable)
            );
        }

        [Fact]
        public void For_DefaultsToDecent_ForAValueTheEnumDoesNotName()
        {
            Assert.IsType<DecentMoveScorer>(CPUPlayingProfileScorers.For((CPUPlayingProfile)42));
        }

        [Fact]
        public void Weights_KeepTheAgreedOrdering()
        {
            // A capture beats every heuristic; sealing a card away is worth slightly more than one capture; the
            // recapture bonus sits below both, and the two-sided case above the one-sided one.
            Assert.True(FormidableMoveScorer.InaccessibleWeight > MoveScorer.CaptureWeight);
            Assert.True(MoveScorer.CaptureWeight > FormidableMoveScorer.RecaptureHigherWeight);
            Assert.True(
                FormidableMoveScorer.RecaptureHigherWeight
                    > FormidableMoveScorer.RecaptureSingleWeight
            );
        }
    }
}
