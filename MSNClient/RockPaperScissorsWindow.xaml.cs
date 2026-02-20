using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MSNShared;

namespace MSNClient
{
    public partial class RockPaperScissorsWindow : Window
    {
        private readonly ClientState _state = App.State;
        private readonly string _gameId;
        private readonly string _opponent;
        private bool _myMoveSubmitted;
        private bool _gameEnded;

        public RockPaperScissorsWindow(string gameId, string opponent)
        {
            InitializeComponent();
            _gameId = gameId;
            _opponent = opponent;

            Player1Name.Text = _state.MyDisplayName;
            var oppContact = _state.GetContact(opponent);
            Player2Name.Text = oppContact?.DisplayName ?? opponent;

            P1MoveText.Text = "";
            P2MoveText.Text = "";

            _state.Net.PacketReceived += OnPacket;
            // Closed += (s, e) => _state.Net.PacketReceived -= OnPacket; // Override to include "Leave"
            Closing += OnWindowClosing;
        }

        private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            _state.Net.PacketReceived -= OnPacket;
            if (!_gameEnded)
            {
                // Tell server we are bailing
                await _state.Net.SendAsync(Packet.Create(PacketType.RockPaperScissors, new RpsPacket
                {
                    Msg = RpsMsgType.Leave,
                    GameId = _gameId,
                    From = _state.MyUsername
                }));
            }
        }

        private void OnPacket(Packet pkt)
        {
            if (pkt.Type != PacketType.RockPaperScissors) return;
            var rps = pkt.GetData<RpsPacket>();
            if (rps == null || rps.GameId != _gameId) return;

            Dispatcher.Invoke(() => HandleRpsPacket(rps));
        }

        private void HandleRpsPacket(RpsPacket pkt)
        {
            switch (pkt.Msg)
            {
                case RpsMsgType.Result:
                    ShowResult(pkt);
                    break;
                case RpsMsgType.GameOver:
                    HandleGameOver(pkt);
                    break;
                case RpsMsgType.InviteDecline: // Opponent left?
                    if (!_gameEnded) // Only show if game wasn't already over
                    {
                        MessageBox.Show("Opponent left the game.", "Game Over", MessageBoxButton.OK);
                        _gameEnded = true;
                        Close();
                    }
                    else
                    {
                        Close();
                    }
                    break;
            }
        }

        private void ShowResult(RpsPacket pkt)
        {
            // Show moves
            P1MoveText.Text = GetEmoji(pkt.Move);
            P2MoveText.Text = GetEmoji(pkt.OpponentMove);

            // Show result text
            if (pkt.Winner == _state.MyUsername)
            {
                ResultText.Text = "You Win! 🎉";
                ResultText.Foreground = Brushes.Green;
            }
            else if (pkt.Winner == _opponent)
            {
                ResultText.Text = "You Lose! 😢";
                ResultText.Foreground = Brushes.Red;
            }
            else
            {
                ResultText.Text = "Draw! 🤝";
                ResultText.Foreground = Brushes.Gray;
            }

            // Update scores
            Player1Score.Text = $"Score: {pkt.P1Score}"; // Note: Server sends P1/P2 based on who initiated. 
                                                         // Wait, P1/P2 in packet refers to Game Player1/Player2.
                                                         // But `RpsManager` sends `Move` as "Your Move" and `OpponentMove` as "Opponent Move".
                                                         // So `P1Score` vs `P2Score` in the packet needs to be interpreted relative to me?
                                                         // `RpsManager` sends: `P1Score` = game.P1Score, `P2Score` = game.P2Score.
                                                         // I need to know if I am P1 or P2.
                                                         // But actually, for simplicity, let's look at `pkt.Winner`. If `Winner` == Me, I increment my displayed score?
                                                         // No, the packet has absolute scores.
                                                         // I don't know if I am P1 or P2 easily from here without storing it.
                                                         // But wait, if `pkt.Winner` is me, I can just increment local counter?
                                                         // Let's rely on packet scores.
                                                         // Issue: Packet has P1Score and P2Score. I don't know who is P1.
                                                         // Let's just track scores locally based on Winner? It's safer.

            // Correction: ResultText logic above uses `pkt.Winner` which is username. That is reliable.
            // So I'll just increment local tracking variables.
            // Or better: ask RpsManager to invite me as P2 if I accepted.
            // But the Window just wants to show "My Score" vs "Opponent Score".

            // Re-read RpsManager.cs ... 
            // `resPkt.P1Score = p1Score; resPkt.P2Score = p2Score;`
            // Start packet had `From` (P1) and `To` (P2).
            // But I didn't save that in Window.
            // Actually, I know `_opponent`.
            // If `pkt.Winner` == `_state.MyUsername`, I score.
            // If `pkt.Winner` == `_opponent`, they score.
        }

        private int _myScore = 0;
        private int _oppScore = 0;

        private void UpdateScores(string winner)
        {
            if (winner == _state.MyUsername) _myScore++;
            else if (winner == _opponent) _oppScore++;

            Player1Score.Text = $"Score: {_myScore}";
            Player2Score.Text = $"Score: {_oppScore}";
        }

        // Override ShowResult to use private trackers
        private async void ShowResultWithDelay(RpsPacket pkt)
        {
            P1MoveText.Text = GetEmoji(pkt.Move);
            P2MoveText.Text = GetEmoji(pkt.OpponentMove);

            string winner = pkt.Winner;

            if (winner == _state.MyUsername)
            {
                ResultText.Text = "You Win! 🎉";
                ResultText.Foreground = Brushes.Green;
                _myScore++;
            }
            else if (winner == _opponent)
            {
                ResultText.Text = "You Lose! 😢";
                ResultText.Foreground = Brushes.Red;
                _oppScore++;
            }
            else
            {
                ResultText.Text = "Draw! 🤝";
                ResultText.Foreground = Brushes.Gray;
            }

            Player1Score.Text = $"Score: {_myScore}";
            Player2Score.Text = $"Score: {_oppScore}";

            await Task.Delay(2000);

            // Reset for next round
            if (!_gameEnded)
            {
                ResetRound();
            }
        }

        private void HandleGameOver(RpsPacket pkt)
        {
            _gameEnded = true;
            // Ensure final move is shown if it came with GameOver? 
            // RpsManager sends Result THEN GameOver.
            // So we just need to show the final message.

            string msg = pkt.Winner == _state.MyUsername ? "🏆 VICTORY! 🏆" : "💀 DEFEAT 💀";
            MessageBox.Show(msg, "Game Over", MessageBoxButton.OK);
            Close();
        }

        private void ResetRound()
        {
            P1MoveText.Text = "?";
            P2MoveText.Text = "?";
            ResultText.Text = "";
            _myMoveSubmitted = false;
            EnableButtons(true);
            P1Status.Text = "Pick a move...";
            P2Status.Text = "Thinking...";
        }

        private string GetEmoji(RpsMove move) => move switch
        {
            RpsMove.Rock => "🪨",
            RpsMove.Paper => "📄",
            RpsMove.Scissors => "✂️",
            _ => "?"
        };

        private async void BtnRock_Click(object sender, RoutedEventArgs e) => await SubmitMove(RpsMove.Rock);
        private async void BtnPaper_Click(object sender, RoutedEventArgs e) => await SubmitMove(RpsMove.Paper);
        private async void BtnScissors_Click(object sender, RoutedEventArgs e) => await SubmitMove(RpsMove.Scissors);

        private async Task SubmitMove(RpsMove move)
        {
            if (_myMoveSubmitted) return;
            _myMoveSubmitted = true;
            EnableButtons(false);

            P1MoveText.Text = GetEmoji(move);
            P1Status.Text = "Waiting for opponent...";

            await _state.Net.SendAsync(Packet.Create(PacketType.RockPaperScissors, new RpsPacket
            {
                Msg = RpsMsgType.Move,
                GameId = _gameId,
                Move = move
            }));
        }

        private void EnableButtons(bool enable)
        {
            BtnRock.IsEnabled = enable;
            BtnPaper.IsEnabled = enable;
            BtnScissors.IsEnabled = enable;
        }
    }
}
