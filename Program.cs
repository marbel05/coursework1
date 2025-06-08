using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace BookRecommendationBot
{
    class Program
    {
        private static TelegramBotClient _botClient;
        private static HttpClient _httpClient = new HttpClient();
        private static Dictionary<long, UserState> _userStates = new Dictionary<long, UserState>();
        private static Dictionary<long, List<Book>> _favoriteBooks = new Dictionary<long, List<Book>>();
        private static Dictionary<long, List<SearchHistoryItem>> _searchHistory = new Dictionary<long, List<SearchHistoryItem>>();
        private const string GoogleBooksApiUrl = "https://www.googleapis.com/books/v1/volumes";

        private static readonly Dictionary<string, string> _genreMappings = new Dictionary<string, string>
        {
            {"Фентезі", "fantasy"},
            {"Драма", "drama"},
            {"Детектив", "detective"},
            {"Наукова фантастика", "science+fiction"},
            {"Роман", "romance"},
            {"Історичний", "history"}
        };

        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting bot...");

            _botClient = new TelegramBotClient("8179144598:AAFdcoAXlwSXail1RDdmtBhGodMCR_VtZ_I");

            var me = await _botClient.GetMeAsync();
            Console.WriteLine($"Bot started: {me.Username}");

            _botClient.StartReceiving(UpdateHandler, ErrorHandler);

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        private static Task ErrorHandler(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Error: {exception.Message}");
            return Task.CompletedTask;
        }

        private static async Task UpdateHandler(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Type == UpdateType.CallbackQuery)
            {
                await HandleCallbackQuery(botClient, update.CallbackQuery);
                return;
            }

            if (update.Type != UpdateType.Message || update.Message.Type != MessageType.Text)
                return;

            var chatId = update.Message.Chat.Id;
            var messageText = update.Message.Text;

            Console.WriteLine($"Received message from {chatId}: {messageText}");

            if (!_userStates.ContainsKey(chatId))
            {
                _userStates[chatId] = UserState.MainMenu;
            }

            if (messageText == "/start")
            {
                await ShowMainMenu(chatId);
                return;
            }

            switch (_userStates[chatId])
            {
                case UserState.MainMenu:
                    await HandleMainMenu(chatId, messageText);
                    break;
                case UserState.ChoosingGenre:
                    await HandleGenreSelection(chatId, messageText);
                    break;
                case UserState.SearchingByTitle:
                    await HandleTitleSearch(chatId, messageText);
                    break;
                case UserState.SearchingByAuthor:
                    await HandleAuthorSearch(chatId, messageText);
                    break;
                case UserState.ViewingFavorites:
                    await HandleFavorites(chatId, messageText);
                    break;
                case UserState.ComparingBooks:
                    await HandleBookComparison(chatId, messageText);
                    break;
                case UserState.ViewingHistory:
                    await HandleHistorySelection(chatId, messageText);
                    break;
            }
        }

        private static async Task HandleCallbackQuery(ITelegramBotClient botClient, CallbackQuery callbackQuery)
        {
            var chatId = callbackQuery.Message.Chat.Id;
            var data = callbackQuery.Data;

            if (data.StartsWith("add_"))
            {
                var bookId = data.Substring(4);
                await AddToFavorites(chatId, bookId);
                await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Книга додана до улюблених ❤️");

                await botClient.EditMessageReplyMarkupAsync(
                    chatId: chatId,
                    messageId: callbackQuery.Message.MessageId,
                    replyMarkup: null);
            }
            else if (data.StartsWith("remove_"))
            {
                var bookId = data.Substring(7);
                await RemoveFromFavorites(chatId, bookId);
                await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "Книга видалена з улюблених");

                await ShowFavorites(chatId);
            }
            else if (data == "back")
            {
                await ShowMainMenu(chatId);
            }
            else if (data.StartsWith("history_"))
            {
                var searchTerm = data.Substring(8);
                await HandleHistoryItemSelection(chatId, searchTerm);
            }
            else if (data == "sort_by_rating")
            {
                await SortAndShowResultsByRating(chatId);
            }
            else if (data == "sort_by_date")
            {
                await SortAndShowResultsByDate(chatId);
            }
        }

        private static async Task SortAndShowResultsByRating(long chatId)
        {
            if (!_searchHistory.ContainsKey(chatId) || _searchHistory[chatId].Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Немає результатів для сортування.");
                return;
            }

            var lastSearch = _searchHistory[chatId].Last();
            if (lastSearch.Books == null || lastSearch.Books.Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Немає результатів для сортування.");
                return;
            }

            var sortedBooks = lastSearch.Books.OrderByDescending(b => b.AverageRating ?? 0).ToList();
            lastSearch.Books = sortedBooks;

            await _botClient.SendTextMessageAsync(chatId, "🔽 Результати відсортовані за рейтингом:");

            foreach (var book in sortedBooks.Take(5))
            {
                await SendBookInfo(chatId, book);
            }
        }

        private static async Task SortAndShowResultsByDate(long chatId)
        {
            if (!_searchHistory.ContainsKey(chatId) || _searchHistory[chatId].Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Немає результатів для сортування.");
                return;
            }

            var lastSearch = _searchHistory[chatId].Last();
            if (lastSearch.Books == null || lastSearch.Books.Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Немає результатів для сортування.");
                return;
            }

            var sortedBooks = lastSearch.Books
                .OrderByDescending(b => b.PublishedDate, new DateStringComparer())
                .ToList();
            lastSearch.Books = sortedBooks;

            await _botClient.SendTextMessageAsync(chatId, "🔽 Результати відсортовані за датою публікації:");

            foreach (var book in sortedBooks.Take(5))
            {
                await SendBookInfo(chatId, book);
            }
        }

        private class DateStringComparer : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                if (x == null && y == null) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                if (DateTime.TryParse(x, out var dateX) && DateTime.TryParse(y, out var dateY))
                {
                    return dateX.CompareTo(dateY);
                }
                return string.Compare(x, y, StringComparison.Ordinal);
            }
        }

        private static async Task AddToFavorites(long chatId, string bookId)
        {
            var book = await GetBookById(bookId);
            if (book == null) return;

            if (!_favoriteBooks.ContainsKey(chatId))
            {
                _favoriteBooks[chatId] = new List<Book>();
            }

            if (!_favoriteBooks[chatId].Any(b => b.Id == bookId))
            {
                _favoriteBooks[chatId].Add(book);
            }
        }

        private static async Task RemoveFromFavorites(long chatId, string bookId)
        {
            if (_favoriteBooks.ContainsKey(chatId))
            {
                var bookToRemove = _favoriteBooks[chatId].FirstOrDefault(b => b.Id == bookId);
                if (bookToRemove != null)
                {
                    _favoriteBooks[chatId].Remove(bookToRemove);
                }
            }
        }

        private static async Task<Book> GetBookById(string bookId)
        {
            try
            {
                var response = await _httpClient.GetFromJsonAsync<GoogleBooksResponse>($"{GoogleBooksApiUrl}/{bookId}");
                if (response == null) return null;

                if (response.Id != null)
                {
                    return MapToBook(response.ToItem());
                }

                return response.Items?.FirstOrDefault() != null ? MapToBook(response.Items.First()) : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting book by ID: {ex.Message}");
                return null;
            }
        }

        private static async Task ShowMainMenu(long chatId)
        {
            _userStates[chatId] = UserState.MainMenu;

            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("📚 Вибрати жанр"), new KeyboardButton("🆕 Нові книжки") },
                new[] { new KeyboardButton("🔍 Пошук за назвою"), new KeyboardButton("👨‍💼 Пошук за автором") },
                new[] { new KeyboardButton("🎲 Випадкова книга"), new KeyboardButton("❤️ Улюблені") },
                new[] { new KeyboardButton("📊 Порівняти книги"), new KeyboardButton("📜 Історія пошуку") }
            })
            {
                ResizeKeyboard = true
            };

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Оберіть опцію:",
                replyMarkup: keyboard);
        }

        private static async Task HandleMainMenu(long chatId, string messageText)
        {
            if (messageText == "📚 Вибрати жанр")
            {
                await ShowGenreSelection(chatId);
            }
            else if (messageText == "🔍 Пошук за назвою")
            {
                _userStates[chatId] = UserState.SearchingByTitle;
                await _botClient.SendTextMessageAsync(chatId, "Введіть назву книги або частину назви:");
            }
            else if (messageText == "👨‍💼 Пошук за автором")
            {
                _userStates[chatId] = UserState.SearchingByAuthor;
                await _botClient.SendTextMessageAsync(chatId, "Введіть ім'я автора:");
            }
            else if (messageText == "🎲 Випадкова книга")
            {
                await GetRandomBook(chatId);
            }
            else if (messageText == "❤️ Улюблені")
            {
                await ShowFavorites(chatId);
            }
            else if (messageText == "📊 Порівняти книги")
            {
                _userStates[chatId] = UserState.ComparingBooks;
                await _botClient.SendTextMessageAsync(chatId, "Введіть назви двох книг через кому (наприклад: 'Гаррі Поттер, Відьмак'):");
            }
            else if (messageText == "📜 Історія пошуку")
            {
                await ShowSearchHistory(chatId);
            }
            else if (messageText == "🆕 Нові книжки")
            {
                await ShowNewBooks(chatId);
            }
            else
            {
                await _botClient.SendTextMessageAsync(chatId, "Не розумію команду. Спробуйте ще раз.");
            }
        }

        private static async Task ShowNewBooks(long chatId)
        {
            await _botClient.SendTextMessageAsync(chatId, "Шукаю найновіші книжки...");

            try
            {
                var response = await _httpClient.GetFromJsonAsync<GoogleBooksResponse>(
                    $"{GoogleBooksApiUrl}?q=*&orderBy=newest&maxResults=5");

                var books = response?.Items?.Select(i => MapToBook(i)).ToList();

                if (books == null || books.Count == 0)
                {
                    await _botClient.SendTextMessageAsync(chatId, "Не вдалося знайти нові книжки.");
                    return;
                }

                if (!_searchHistory.ContainsKey(chatId))
                {
                    _searchHistory[chatId] = new List<SearchHistoryItem>();
                }

                _searchHistory[chatId].Add(new SearchHistoryItem
                {
                    SearchTerm = "Нові книжки",
                    Books = books,
                    SearchDate = DateTime.Now
                });

                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("Сортувати за рейтингом ⭐", "sort_by_rating"),
                    InlineKeyboardButton.WithCallbackData("Сортувати за датою 📅", "sort_by_date")
                });

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: "📚 Ось найновіші книжки:",
                    replyMarkup: keyboard);

                foreach (var book in books)
                {
                    await SendBookInfo(chatId, book);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting new books: {ex.Message}");
                await _botClient.SendTextMessageAsync(chatId, "Сталася помилка при пошуку нових книжок.");
            }
        }

        private static async Task ShowSearchHistory(long chatId)
        {
            _userStates[chatId] = UserState.ViewingHistory;

            if (!_searchHistory.ContainsKey(chatId) || _searchHistory[chatId].Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Ваша історія пошуку порожня.");
                await ShowMainMenu(chatId);
                return;
            }

            var keyboard = new InlineKeyboardMarkup(_searchHistory[chatId]
                .Select((item, index) => InlineKeyboardButton.WithCallbackData(
                    $"{index + 1}. {item.SearchTerm} ({item.Books?.Count ?? 0} книг)",
                    $"history_{item.SearchTerm}"))
                .Concat(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "back") })
                .Chunk(1)
                .ToArray());

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Ваша історія пошуку:",
                replyMarkup: keyboard);
        }

        private static async Task HandleHistoryItemSelection(long chatId, string searchTerm)
        {
            var historyItem = _searchHistory[chatId].FirstOrDefault(x => x.SearchTerm == searchTerm);
            if (historyItem == null || historyItem.Books == null || historyItem.Books.Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Не вдалося знайти цей запис в історії.");
                return;
            }

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData("Сортувати за рейтингом ⭐", "sort_by_rating"),
                InlineKeyboardButton.WithCallbackData("Сортувати за датою 📅", "sort_by_date"),
                InlineKeyboardButton.WithCallbackData("⬅️ Назад", "back")
            });

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: $"Знайдено {historyItem.Books.Count} книг за запитом '{searchTerm}':",
                replyMarkup: keyboard);

            foreach (var book in historyItem.Books.Take(5))
            {
                await SendBookInfo(chatId, book);
            }
        }

        private static async Task ShowGenreSelection(long chatId)
        {
            _userStates[chatId] = UserState.ChoosingGenre;

            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new[] { new KeyboardButton("Фентезі"), new KeyboardButton("Драма") },
                new[] { new KeyboardButton("Детектив"), new KeyboardButton("Наукова фантастика") },
                new[] { new KeyboardButton("Роман"), new KeyboardButton("Історичний") },
                new[] { new KeyboardButton("⬅️ Назад") }
            })
            {
                ResizeKeyboard = true
            };

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Оберіть жанр:",
                replyMarkup: keyboard);
        }

        private static async Task HandleGenreSelection(long chatId, string messageText)
        {
            if (messageText == "⬅️ Назад")
            {
                await ShowMainMenu(chatId);
                return;
            }

            if (_genreMappings.ContainsKey(messageText))
            {
                await _botClient.SendTextMessageAsync(chatId, $"Шукаю книги у жанрі {messageText}...");
                var books = await GetBooksByGenre(_genreMappings[messageText]);

                if (books == null || books.Count == 0)
                {
                    await _botClient.SendTextMessageAsync(chatId, "Не знайдено книг у цьому жанрі.");
                    return;
                }

                if (!_searchHistory.ContainsKey(chatId))
                {
                    _searchHistory[chatId] = new List<SearchHistoryItem>();
                }

                _searchHistory[chatId].Add(new SearchHistoryItem
                {
                    SearchTerm = $"Жанр: {messageText}",
                    Books = books,
                    SearchDate = DateTime.Now
                });

                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("Сортувати за рейтингом ⭐", "sort_by_rating"),
                    InlineKeyboardButton.WithCallbackData("Сортувати за датою 📅", "sort_by_date")
                });

                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"Знайдено {books.Count} книг у жанрі {messageText}:",
                    replyMarkup: keyboard);

                foreach (var book in books.Take(5))
                {
                    await SendBookInfo(chatId, book);
                }
            }
            else
            {
                await _botClient.SendTextMessageAsync(chatId, "Будь ласка, оберіть жанр зі списку.");
            }
        }

        private static async Task HandleTitleSearch(long chatId, string messageText)
        {
            await _botClient.SendTextMessageAsync(chatId, $"Шукаю книги за назвою '{messageText}'...");

            var books = await SearchBooksByTitle(messageText);

            if (books == null || books.Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Не знайдено книг за цією назвою.");
                _userStates[chatId] = UserState.MainMenu;
                return;
            }

            if (!_searchHistory.ContainsKey(chatId))
            {
                _searchHistory[chatId] = new List<SearchHistoryItem>();
            }

            _searchHistory[chatId].Add(new SearchHistoryItem
            {
                SearchTerm = $"Назва: {messageText}",
                Books = books,
                SearchDate = DateTime.Now
            });

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData("Сортувати за рейтингом ⭐", "sort_by_rating"),
                InlineKeyboardButton.WithCallbackData("Сортувати за датою 📅", "sort_by_date")
            });

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: $"Знайдено {books.Count} книг за назвою '{messageText}':",
                replyMarkup: keyboard);

            foreach (var book in books.Take(5))
            {
                await SendBookInfo(chatId, book);
            }

            _userStates[chatId] = UserState.MainMenu;
        }

        private static async Task HandleAuthorSearch(long chatId, string messageText)
        {
            await _botClient.SendTextMessageAsync(chatId, $"Шукаю книги автора '{messageText}'...");

            var books = await SearchBooksByAuthor(messageText);

            if (books == null || books.Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Не знайдено книг цього автора.");
                _userStates[chatId] = UserState.MainMenu;
                return;
            }

            if (!_searchHistory.ContainsKey(chatId))
            {
                _searchHistory[chatId] = new List<SearchHistoryItem>();
            }

            _searchHistory[chatId].Add(new SearchHistoryItem
            {
                SearchTerm = $"Автор: {messageText}",
                Books = books,
                SearchDate = DateTime.Now
            });

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData("Сортувати за рейтингом ⭐", "sort_by_rating"),
                InlineKeyboardButton.WithCallbackData("Сортувати за датою 📅", "sort_by_date")
            });

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: $"Знайдено {books.Count} книг автора '{messageText}':",
                replyMarkup: keyboard);

            foreach (var book in books.Take(5))
            {
                await SendBookInfo(chatId, book);
            }

            _userStates[chatId] = UserState.MainMenu;
        }

        private static async Task GetRandomBook(long chatId)
        {
            await _botClient.SendTextMessageAsync(chatId, "Шукаю випадкову книгу...");

            var random = new Random();
            var randomGenre = _genreMappings.Values.ElementAt(random.Next(_genreMappings.Count));

            var books = await GetBooksByGenre(randomGenre);

            if (books == null || books.Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "Не вдалося знайти випадкову книгу. Спробуйте ще раз.");
                return;
            }

            var randomBook = books[random.Next(books.Count)];
            await SendBookInfo(chatId, randomBook);
        }

        private static async Task ShowFavorites(long chatId)
        {
            _userStates[chatId] = UserState.ViewingFavorites;

            if (!_favoriteBooks.ContainsKey(chatId) || _favoriteBooks[chatId].Count == 0)
            {
                await _botClient.SendTextMessageAsync(chatId, "У вас немає улюблених книг.");
                await ShowMainMenu(chatId);
                return;
            }

            var keyboard = new InlineKeyboardMarkup(_favoriteBooks[chatId]
                .Select((book, index) => InlineKeyboardButton.WithCallbackData(
                    $"{index + 1}. {book.Title}",
                    $"remove_{book.Id}"))
                .Concat(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "back") })
                .Chunk(1)
                .ToArray());

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "Ваші улюблені книги:",
                replyMarkup: keyboard);
        }

        private static async Task HandleFavorites(long chatId, string messageText)
        {
            if (messageText == "⬅️ Назад")
            {
                await ShowMainMenu(chatId);
            }
        }

        private static async Task HandleHistorySelection(long chatId, string messageText)
        {
            if (messageText == "⬅️ Назад")
            {
                await ShowMainMenu(chatId);
            }
        }

        private static async Task HandleBookComparison(long chatId, string messageText)
        {
            var bookTitles = messageText.Split(',').Select(t => t.Trim()).ToArray();

            if (bookTitles.Length != 2)
            {
                await _botClient.SendTextMessageAsync(chatId, "Будь ласка, введіть рівно дві назви книг через кому.");
                return;
            }

            await _botClient.SendTextMessageAsync(chatId, $"Порівнюю книги '{bookTitles[0]}' та '{bookTitles[1]}'...");

            var book1 = (await SearchBooksByTitle(bookTitles[0]))?.FirstOrDefault();
            var book2 = (await SearchBooksByTitle(bookTitles[1]))?.FirstOrDefault();

            if (book1 == null || book2 == null)
            {
                await _botClient.SendTextMessageAsync(chatId, "Не вдалося знайти одну з книг. Спробуйте інші назви.");
                _userStates[chatId] = UserState.MainMenu;
                return;
            }

            var comparisonText = new StringBuilder();
            comparisonText.AppendLine("📊 Порівняння книг:");
            comparisonText.AppendLine();
            comparisonText.AppendLine($"📖 1. {book1.Title} vs 2. {book2.Title}");
            comparisonText.AppendLine();
            comparisonText.AppendLine($"⭐ Рейтинг: {book1.AverageRating?.ToString("0.0") ?? "Н/Д"} vs {book2.AverageRating?.ToString("0.0") ?? "Н/Д"}");
            comparisonText.AppendLine($"📅 Дата публікації: {book1.PublishedDate ?? "Н/Д"} vs {book2.PublishedDate ?? "Н/Д"}");
            comparisonText.AppendLine($"📝 Кількість сторінок: {book1.PageCount?.ToString() ?? "Н/Д"} vs {book2.PageCount?.ToString() ?? "Н/Д"}");
            comparisonText.AppendLine();
            comparisonText.AppendLine("🔍 Короткий опис:");
            comparisonText.AppendLine($"1. {book1.Description?.Substring(0, Math.Min(100, book1.Description.Length)) ?? "Опис відсутній"}...");
            comparisonText.AppendLine($"2. {book2.Description?.Substring(0, Math.Min(100, book2.Description.Length)) ?? "Опис відсутній"}...");

            await _botClient.SendTextMessageAsync(chatId, comparisonText.ToString());

            _userStates[chatId] = UserState.MainMenu;
        }

        private static async Task SendBookInfo(long chatId, Book book)
        {
            var message = new StringBuilder();
            message.AppendLine($"📖 <b>{book.Title}</b>");
            message.AppendLine($"👨‍💼 Автор: {book.Authors ?? "Невідомо"}");
            message.AppendLine($"⭐ Рейтинг: {book.AverageRating?.ToString("0.0") ?? "Н/Д"}");
            message.AppendLine($"📅 Дата публікації: {book.PublishedDate ?? "Невідомо"}");
            message.AppendLine($"📝 Сторінок: {book.PageCount?.ToString() ?? "Невідомо"}");
            message.AppendLine();
            message.AppendLine($"📖 Опис: {book.Description?.Substring(0, Math.Min(200, book.Description.Length)) ?? "Опис відсутній"}...");

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData("❤️ Додати до улюблених", $"add_{book.Id}")
            });

            try
            {
                if (!string.IsNullOrEmpty(book.ThumbnailUrl))
                {
                    await _botClient.SendPhotoAsync(
                        chatId: chatId,
                        photo: book.ThumbnailUrl,
                        caption: message.ToString(),
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard);
                }
                else
                {
                    await _botClient.SendTextMessageAsync(
                        chatId: chatId,
                        text: message.ToString(),
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending book info: {ex.Message}");
                await _botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: message.ToString(),
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard);
            }
        }

        private static async Task<List<Book>> GetBooksByGenre(string genre)
        {
            try
            {
                var response = await _httpClient.GetFromJsonAsync<GoogleBooksResponse>($"{GoogleBooksApiUrl}?q=subject:{genre}&maxResults=5&orderBy=newest");
                return response?.Items?.Select(i => MapToBook(i)).ToList() ?? new List<Book>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting books by genre: {ex.Message}");
                return null;
            }
        }

        private static async Task<List<Book>> SearchBooksByTitle(string title)
        {
            try
            {
                var encodedTitle = Uri.EscapeDataString(title);
                var response = await _httpClient.GetFromJsonAsync<GoogleBooksResponse>($"{GoogleBooksApiUrl}?q=intitle:{encodedTitle}&maxResults=5");
                return response?.Items?.Select(i => MapToBook(i)).ToList() ?? new List<Book>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error searching books by title: {ex.Message}");
                return null;
            }
        }

        private static async Task<List<Book>> SearchBooksByAuthor(string author)
        {
            try
            {
                var encodedAuthor = Uri.EscapeDataString(author);
                var response = await _httpClient.GetFromJsonAsync<GoogleBooksResponse>($"{GoogleBooksApiUrl}?q=inauthor:{encodedAuthor}&maxResults=5");
                return response?.Items?.Select(i => MapToBook(i)).ToList() ?? new List<Book>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error searching books by author: {ex.Message}");
                return null;
            }
        }

        private static Book MapToBook(Item item)
        {
            if (item == null) return null;

            return new Book
            {
                Id = item.Id,
                Title = item.VolumeInfo?.Title ?? "Без назви",
                Authors = item.VolumeInfo?.Authors != null ? string.Join(", ", item.VolumeInfo.Authors) : "Невідомо",
                Description = item.VolumeInfo?.Description,
                PublishedDate = item.VolumeInfo?.PublishedDate,
                AverageRating = item.VolumeInfo?.AverageRating,
                PageCount = item.VolumeInfo?.PageCount,
                ThumbnailUrl = item.VolumeInfo?.ImageLinks?.Thumbnail?.Replace("http://", "https://")
            };
        }
    }

    public class Book
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Authors { get; set; }
        public string Description { get; set; }
        public string PublishedDate { get; set; }
        public double? AverageRating { get; set; }
        public int? PageCount { get; set; }
        public string ThumbnailUrl { get; set; }
    }

    public class SearchHistoryItem
    {
        public string SearchTerm { get; set; }
        public List<Book> Books { get; set; }
        public DateTime SearchDate { get; set; }
    }

    public class GoogleBooksResponse
    {
        public List<Item> Items { get; set; }
        public string Id { get; set; }
        public VolumeInfo VolumeInfo { get; set; }

        public Item ToItem()
        {
            return new Item
            {
                Id = this.Id,
                VolumeInfo = this.VolumeInfo
            };
        }
    }

    public class Item
    {
        public string Id { get; set; }
        public VolumeInfo VolumeInfo { get; set; }
    }

    public class VolumeInfo
    {
        public string Title { get; set; }
        public List<string> Authors { get; set; }
        public string Description { get; set; }
        public string PublishedDate { get; set; }
        public double? AverageRating { get; set; }
        public int? PageCount { get; set; }
        public ImageLinks ImageLinks { get; set; }
        public List<string> Categories { get; set; }
    }

    public class ImageLinks
    {
        public string Thumbnail { get; set; }
    }

    public enum UserState
    {
        MainMenu,
        ChoosingGenre,
        SearchingByTitle,
        SearchingByAuthor,
        ViewingFavorites,
        ComparingBooks,
        ViewingHistory
    }
}