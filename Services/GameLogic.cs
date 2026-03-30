using System.Collections.Generic;
using System.Linq;

namespace WordGuessAPI.Services;

public class GameLogic
{
    private string _currentWord = string.Empty;
    private HashSet<char> _guessedLetters = new();
    private HashSet<char> _wrongGuesses = new();
    private const int MaxAttempts = 6;
    private bool _gameActive = true;
    private string? _winner;

    public void ResetGame(string difficulty, List<string> wordsPool)
    {
        if (wordsPool == null || wordsPool.Count == 0)
        {
            // Fallback
            wordsPool = new List<string> { "WORDGUESS" };
        }
        var random = new Random();
        _currentWord = wordsPool[random.Next(wordsPool.Count)].ToUpper();
        _guessedLetters.Clear();
        _wrongGuesses.Clear();
        _gameActive = true;
        _winner = null;
    }

    public string MaskedWord => string.Join(" ", _currentWord.Select(c => _guessedLetters.Contains(c) ? c : '_'));
    public int AttemptsLeft => MaxAttempts - _wrongGuesses.Count;
    public IEnumerable<char> WrongGuesses => _wrongGuesses;
    public IEnumerable<char> GuessedLetters => _guessedLetters;
    public bool GameActive => _gameActive;
    public string? Winner => _winner;

    public object GetState() => new
    {
        maskedWord = MaskedWord,
        attemptsLeft = AttemptsLeft,
        wrongGuesses = WrongGuesses,
        guessedLetters = GuessedLetters,
        gameActive = _gameActive,
        winner = _winner
    };

    public (bool success, string? maskedWord, int attemptsLeft, bool gameOver, string? winner, string? error) GuessLetter(char letter)
    {
        letter = char.ToUpper(letter);
        if (!_gameActive)
            return (false, null, 0, true, null, "El juego ya terminó.");
        if (_guessedLetters.Contains(letter) || _wrongGuesses.Contains(letter))
            return (false, null, 0, false, null, "Ya se intentó esa letra.");

        if (_currentWord.Contains(letter))
            _guessedLetters.Add(letter);
        else
            _wrongGuesses.Add(letter);

        bool gameOver = CheckGameOver();
        return (true, MaskedWord, AttemptsLeft, gameOver, _winner, null);
    }

    public (bool success, string? maskedWord, bool gameOver, string? winner, string? message, string? error) GuessWord(string word, string playerName)
    {
        word = word.ToUpper();
        if (!_gameActive)
            return (false, null, true, null, null, "El juego ya terminó.");
        if (word == _currentWord)
        {
            _gameActive = false;
            _winner = playerName;
            return (true, _currentWord, true, _winner, $"¡{playerName} adivinó la palabra!", null);
        }
        return (false, null, false, null, null, "Palabra incorrecta.");
    }

    private bool CheckGameOver()
    {
        if (!MaskedWord.Contains('_'))
        {
            _gameActive = false;
            _winner = "Todos";
            return true;
        }
        if (_wrongGuesses.Count >= MaxAttempts)
        {
            _gameActive = false;
            _winner = null;
            return true;
        }
        return false;
    }
}