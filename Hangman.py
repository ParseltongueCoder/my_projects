import random
import hangman_words
import hangman_art


def initialize_game():
    """Initializes the game and returns the chosen word and initial display."""
    print(hangman_art.logo)
    chosen_word = random.choice(hangman_words.word_list)
    display = ["_"] * len(chosen_word)  # Placeholder for guessed letters
    return chosen_word, display


def get_valid_guess(guessed_letters):
    """Prompts the user for a valid guess and checks if its guessed already."""
    while True:
        guess = input("Guess a letter: ").lower()
        if len(guess) != 1 or not guess.isalpha():
            print("Invalid input. Please enter a single alphabetical character.")
        elif guess in guessed_letters:
            print(f"You've already guessed '{guess}'. Try a different letter.")
        else:
            return guess


def update_display(chosen_word, display, guess):
    """Updates the display with correctly guessed letters."""
    for index, letter in enumerate(chosen_word):
        if letter == guess:
            display[index] = letter
    return display


def play_hangman():
    """Main function to play the Hangman game."""
    # Initialize game variables
    chosen_word, display = initialize_game()
    guessed_letters = set()
    lives = 6
    game_over = False

    print("Word to guess: " + " ".join(display))

    while not game_over:
        print(f"\n**************************** {lives} LIVES LEFT ****************************")
        guess = get_valid_guess(guessed_letters)
        guessed_letters.add(guess)

        if guess in chosen_word:
            display = update_display(chosen_word, display, guess)
            print(f"Correct guess! The letter '{guess}' is in the word.")
        else:
            lives -= 1
            print(f"Wrong guess! The letter '{guess}' is not in the word. You lose a life.")

        print("Word to guess: " + " ".join(display))
        print(hangman_art.stages[lives])

        # Check game outcome
        if "_" not in display:
            game_over = True
            print("**************************** YOU WIN ****************************")
        elif lives == 0:
            game_over = True
            print(f"You lose! The correct word was: {chosen_word}")


# Start the game
play_hangman()