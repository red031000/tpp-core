using System.Collections.Generic;
using System.Linq;
using ArgsParsing;
using ArgsParsing.TypeParsers;
using Core.Chat;
using Core.Commands;
using Core.Commands.Definitions;
using Core.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NodaTime;
using Persistence.Models;
using Persistence.MongoDB;
using Persistence.MongoDB.Repos;
using Persistence.MongoDB.Serializers;
using Persistence.Repos;

namespace Core
{
    /// <summary>
    /// Bundling up boilerplate code required to construct various classes.
    /// </summary>
    public static class Setups
    {
        public static ArgsParser SetUpArgsParser(IUserRepo userRepo, PokedexData pokedexData)
        {
            var argsParser = new ArgsParser();
            argsParser.AddArgumentParser(new IntParser());
            argsParser.AddArgumentParser(new StringParser());
            argsParser.AddArgumentParser(new InstantParser());
            argsParser.AddArgumentParser(new TimeSpanParser());
            argsParser.AddArgumentParser(new HexColorParser());
            argsParser.AddArgumentParser(new PokeyenParser());
            argsParser.AddArgumentParser(new TokensParser());
            argsParser.AddArgumentParser(new PkmnSpeciesParser(pokedexData.KnownSpecies, PokedexData.NormalizeName));

            argsParser.AddArgumentParser(new AnyOrderParser(argsParser));
            argsParser.AddArgumentParser(new OneOfParser(argsParser));
            argsParser.AddArgumentParser(new OptionalParser(argsParser));

            argsParser.AddArgumentParser(new UserParser(userRepo));
            return argsParser;
        }

        public static CommandProcessor SetUpCommandProcessor(
            ILoggerFactory loggerFactory,
            ArgsParser argsParser,
            Databases databases,
            StopToken stopToken,
            ChatConfig chatConfig,
            TwitchChat chat)
        {
            var commandProcessor = new CommandProcessor(
                loggerFactory.CreateLogger<CommandProcessor>(),
                databases.CommandLogger, argsParser);

            IEnumerable<Command> commands = new[]
            {
                new EasterEggCommands().Commands,
                new StaticResponseCommands().Commands,
                new UserCommands(
                    databases.UserRepo, pokeyenBank: databases.PokeyenBank, tokenBank: databases.TokensBank).Commands,
                new BadgeCommands(databases.BadgeRepo, databases.UserRepo).Commands,
                new OperatorCommands(stopToken, chatConfig.OperatorNames).Commands,
                new ModeratorCommands(chatConfig.ModeratorNames, chatConfig.OperatorNames, chat).Commands,
                new MiscCommands().Commands,
            }.SelectMany(cmds => cmds).Concat(new[]
            {
                new HelpCommand(commandProcessor).Command
            });
            foreach (Command command in commands)
            {
                commandProcessor.InstallCommand(command);
            }
            return commandProcessor;
        }

        public record Databases(
            IUserRepo UserRepo,
            IBadgeRepo BadgeRepo,
            IBank<User> PokeyenBank,
            IBank<User> TokensBank,
            ICommandLogger CommandLogger,
            IMessagequeueRepo MessagequeueRepo,
            IMessagelogRepo MessagelogRepo
        );

        public static Databases SetUpRepositories(BaseConfig baseConfig)
        {
            CustomSerializers.RegisterAll();
            IMongoClient mongoClient = new MongoClient(baseConfig.MongoDbConnectionUri);
            IMongoDatabase mongoDatabase = mongoClient.GetDatabase(baseConfig.MongoDbDatabaseName);
            IMongoDatabase mongoDatabaseMessagelog = mongoClient.GetDatabase(baseConfig.MongoDbDatabaseNameMessagelog);
            IUserRepo userRepo = new UserRepo(
                database: mongoDatabase,
                startingPokeyen: baseConfig.StartingPokeyen,
                startingTokens: baseConfig.StartingTokens);
            IBadgeRepo badgeRepo = new BadgeRepo(
                database: mongoDatabase);
            IBank<User> pokeyenBank = new Bank<User>(
                database: mongoDatabase,
                currencyCollectionName: UserRepo.CollectionName,
                transactionLogCollectionName: "pokeyentransactions",
                u => u.Pokeyen,
                u => u.Id,
                clock: SystemClock.Instance);
            IBank<User> tokenBank = new Bank<User>(
                database: mongoDatabase,
                currencyCollectionName: UserRepo.CollectionName,
                transactionLogCollectionName: "tokentransactions",
                u => u.Tokens,
                u => u.Id,
                clock: SystemClock.Instance);
            tokenBank.AddReservedMoneyChecker(
                new PersistedReservedMoneyCheckers(mongoDatabase).AllDatabaseReservedTokens);
            return new Databases
            (
                UserRepo: userRepo,
                BadgeRepo: badgeRepo,
                PokeyenBank: pokeyenBank,
                TokensBank: tokenBank,
                CommandLogger: new CommandLogger(mongoDatabase, SystemClock.Instance),
                MessagequeueRepo: new MessagequeueRepo(mongoDatabase),
                MessagelogRepo: new MessagelogRepo(mongoDatabaseMessagelog)
            );
        }
    }
}
