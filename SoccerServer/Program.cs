using Fleck;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json;
using System.Diagnostics;

class PlayerInput
{
    public float h { get; set; }
    public float v { get; set; } // v > 0 significa pulo
    public bool kick { get; set; }
}

class GameState
{
    public float player1X { get; set; }
    public float player1Y { get; set; }
    public float player2X { get; set; }
    public float player2Y { get; set; }
    public float ballX { get; set; }
    public float ballY { get; set; }
    public int scoreLeft { get; set; }
    public int scoreRight { get; set; }
    public string lastEvent { get; set; } = "";
}

class Player
{
    public IWebSocketConnection Socket { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public PlayerInput LastInput { get; set; } = new PlayerInput();
}

class Match
{
    public Player Player1 { get; set; }
    public Player Player2 { get; set; }

    // Estado do jogo
    private float player1X, player1Y, player1VelY;
    private float player2X, player2Y, player2VelY;
    private float ballX, ballY, ballVelX, ballVelY;
    private int scoreLeft = 0, scoreRight = 0;

    private const float fieldWidth = 17f;
    private const float fieldHeight = 10f;
    private const float groundY = -5f;
    private const float playerRadius = 1f;
    private const float ballRadius = 0.5f;
    private const float jumpHeight = 8f;
    private const float kickPower = 15f;

    private float halfWidth = fieldWidth / 2f;
    private float halfHeight = fieldHeight / 2f;

    private bool running = true;

    public Match(Player p1, Player p2)
    {
        Player1 = p1;
        Player2 = p2;

        // Inicializa posições
        player1X = -halfWidth + 2f;
        player1Y = groundY + playerRadius;

        player2X = halfWidth - 2f;
        player2Y = groundY + playerRadius;

        ballX = 0f;
        ballY = groundY + ballRadius;

        // Start da thread de simulação
        new Thread(RunMatch).Start();
    }

    private void RunMatch()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        double lastTime = stopwatch.Elapsed.TotalSeconds;

        double sendAccumulator = 0;

        while (running)
        {
            double now = stopwatch.Elapsed.TotalSeconds;
            float deltaTime = (float)(now - lastTime);
            lastTime = now;

            ApplyInput(Player1, ref player1X, ref player1Y, ref player1VelY, deltaTime);
            ApplyInput(Player2, ref player2X, ref player2Y, ref player2VelY, deltaTime);

            // Física da bola
            ballVelY -= 9.8f * deltaTime;
            ballX += ballVelX * deltaTime;
            ballY += ballVelY * deltaTime;

            // colisão chão/teto da bola
            if (ballY - ballRadius < groundY)
            {
                ballY = groundY + ballRadius;
                ballVelY = -ballVelY * 0.6f;
                ballVelX *= 0.9f;
            }
            if (ballY + ballRadius > halfHeight)
            {
                ballY = halfHeight - ballRadius;
                ballVelY = -ballVelY * 0.8f;
            }

            // Detecção de gol
            string lastEvent = "";
            bool leftGoal = (ballX - ballRadius < -halfWidth) && (ballY - ballRadius >= groundY) && (ballY + ballRadius <= halfHeight);
            bool rightGoal = (ballX + ballRadius > halfWidth) && (ballY - ballRadius >= groundY) && (ballY + ballRadius <= halfHeight);

            if (leftGoal)
            {
                scoreRight++;
                lastEvent = "GoalRight";
                ResetBall();
            }
            else if (rightGoal)
            {
                scoreLeft++;
                lastEvent = "GoalLeft";
                ResetBall();
            }

            // colisão lateral fora do gol
            if (!leftGoal && ballX - ballRadius < -halfWidth)
            {
                ballX = -halfWidth + ballRadius;
                ballVelX = -ballVelX * 0.7f;
            }
            if (!rightGoal && ballX + ballRadius > halfWidth)
            {
                ballX = halfWidth - ballRadius;
                ballVelX = -ballVelX * 0.7f;
            }

            ballVelX *= 0.995f;

            // envia estado 30 FPS
            sendAccumulator += deltaTime;
            if (sendAccumulator >= 1.0 / 30.0)
            {
                SendState(lastEvent);
                sendAccumulator = 0;
            }

            Thread.Sleep(1);
        }
    }

    private void ApplyInput(Player player, ref float px, ref float py, ref float velY, float deltaTime)
    {
        var input = player.LastInput;

        // Movimento horizontal
        px += input.h * 5f * deltaTime;
        px = Math.Clamp(px, -halfWidth + playerRadius, halfWidth - playerRadius);

        // Pulo
        if (input.v > 0 && py <= groundY + playerRadius + 0.01f)
        {
            velY = jumpHeight;
        }

        // Chute
        float dx = ballX - px;
        float dy = ballY - py;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (input.kick && dist < 1.5f)
        {
            ballVelX = dx / dist * kickPower;
            ballVelY = dy / dist * kickPower;
        }

        // Física vertical do player
        velY -= 9.8f * deltaTime;
        py += velY * deltaTime;

        if (py - playerRadius < groundY)
        {
            py = groundY + playerRadius;
            velY = 0f;
        }
        if (py + playerRadius > halfHeight)
        {
            py = halfHeight - playerRadius;
            velY = 0f;
        }
    }

    private void SendState(string lastEvent)
    {
        var state = new GameState
        {
            player1X = player1X,
            player1Y = player1Y,
            player2X = player2X,
            player2Y = player2Y,
            ballX = ballX,
            ballY = ballY,
            scoreLeft = scoreLeft,
            scoreRight = scoreRight,
            lastEvent = lastEvent
        };

        Player1.Socket.Send(JsonSerializer.Serialize(state));
        Player2.Socket.Send(JsonSerializer.Serialize(state));
    }

    private void ResetBall()
    {
        ballX = 0f;
        ballY = groundY + ballRadius + 0.2f;
        ballVelX = 0f;
        ballVelY = 0f;
    }

    public void Stop()
    {
        running = false;
    }
}

class MatchmakingServer
{
    private List<Player> waitingPlayers = new List<Player>();
    private List<Match> activeMatches = new List<Match>();
    private object locker = new object();

    public void Start(int port = 8080)
    {
        var server = new WebSocketServer($"ws://0.0.0.0:{port}");
        server.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                var player = new Player { Socket = socket };
                Console.WriteLine($"[MATCHMAKING] Jogador conectado: {player.Id}");
                AddPlayer(player);

                socket.OnMessage = msg =>
                {
                    try
                    {
                        var input = JsonSerializer.Deserialize<PlayerInput>(msg);
                        if (input != null)
                        {
                            player.LastInput = input;
                        }
                    }
                    catch { }
                };

                socket.OnClose = () =>
                {
                    RemovePlayer(player);
                };
            };
        });

        Console.WriteLine("[MATCHMAKING] Servidor rodando...");
    }

    private void AddPlayer(Player player)
    {
        lock (locker)
        {
            waitingPlayers.Add(player);
            TryCreateMatch();
        }
    }

    private void RemovePlayer(Player player)
    {
        lock (locker)
        {
            waitingPlayers.Remove(player);
            foreach (var match in activeMatches.ToArray())
            {
                if (match.Player1 == player || match.Player2 == player)
                {
                    match.Stop();
                    var other = match.Player1 == player ? match.Player2 : match.Player1;
                    other.Socket.Send("{\"event\":\"OpponentDisconnected\"}");
                    activeMatches.Remove(match);
                    Console.WriteLine("[MATCHMAKING] Partida encerrada por desconexão!");
                }
            }
        }
    }

    private void TryCreateMatch()
    {
        while (waitingPlayers.Count >= 2)
        {
            var p1 = waitingPlayers[0];
            var p2 = waitingPlayers[1];
            waitingPlayers.RemoveRange(0, 2);

            var match = new Match(p1, p2);
            activeMatches.Add(match);

            Console.WriteLine($"[MATCHMAKING] Partida criada: {p1.Id} vs {p2.Id}");

            p1.Socket.Send("{\"event\":\"MatchFound\",\"playerNumber\":1}");
            p2.Socket.Send("{\"event\":\"MatchFound\",\"playerNumber\":2}");
        }
    }
}

class Program
{
    static void Main()
    {
        var matchmaking = new MatchmakingServer();
        matchmaking.Start(8080);
        Console.ReadLine();
    }
}
