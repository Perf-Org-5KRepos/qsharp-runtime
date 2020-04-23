﻿namespace @Namespace
{
    using Microsoft.Quantum.Simulation.Core;
    using Microsoft.Quantum.Simulation.Simulators;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.CommandLine;
    using System.CommandLine.Builder;
    using System.CommandLine.Help;
    using System.CommandLine.Invocation;
    using System.CommandLine.Parsing;
    using System.Linq;
    using System.Numerics;
    using System.Threading.Tasks;

    /// <summary>
    /// The entry point driver is the entry point for the C# application that executes the Q# entry point.
    /// </summary>
    internal static class Driver
    {
        /// <summary>
        /// The argument handler for the Q# Unit type.
        /// </summary>
        internal static Argument<QVoid> UnitArgumentHandler
        {
            get
            {
                var arg = new Argument<QVoid>(CreateArgumentParser(value =>
                    value.Trim() == QVoid.Instance.ToString() ? (true, QVoid.Instance) : (false, default)));
                arg.AddSuggestions(new[] { QVoid.Instance.ToString() });
                return arg;
            }
        }

        /// <summary>
        /// The argument handler for the Q# Result type.
        /// </summary>
        internal static Argument<Result> ResultArgumentHandler
        {
            get
            {
                var arg = new Argument<Result>(CreateArgumentParser(value =>
                    Enum.TryParse(value, ignoreCase: true, out ResultValue result) ? result switch
                    {
                        ResultValue.Zero => (true, Result.Zero),
                        ResultValue.One => (true, Result.One),
                        _ => (false, default)
                    } : (false, default)));
                arg.AddSuggestions(new[] { ResultValue.Zero.ToString(), ResultValue.One.ToString() });
                return arg;
            }
        }

        /// <summary>
        /// The argument handler for the Q# BigInt type.
        /// </summary>
        internal static Argument<BigInteger> BigIntArgumentHandler => new Argument<BigInteger>(
            CreateArgumentParser(value =>
                BigInteger.TryParse(value, out var result) ? (true, result) : (false, default)));

        /// <summary>
        /// The argument handler for the Q# Range type.
        /// </summary>
        internal static Argument<QRange> RangeArgumentHandler => new Argument<QRange>(result =>
        {
            var option = ((OptionResult)result.Parent).Token.Value;
            var value = result.Tokens.Single().Value;
            var range = ParseRangeFromEnumerable(option, value, value.Split(".."));
            if (range.IsFailure)
            {
                result.ErrorMessage = range.ErrorMessage;
            }
            return range.ValueOrDefault;
        })
        {
            Arity = ArgumentArity.ExactlyOne
        };

        /// <summary>
        /// Runs the entry point.
        /// </summary>
        /// <param name="args">The command-line arguments.</param>
        /// <returns>The exit code.</returns>
        private static async Task<int> Main(string[] args)
        {
            var simulate = new Command("simulate", "(default) Run the program using a local simulator.");
            TryCreateOption(
                Constants.SimulatorOptions,
                () => EntryPoint.DefaultSimulator,
                "The name of the simulator to use.").Then(option =>
                {
                    option.Argument.AddSuggestions(ImmutableHashSet<string>.Empty
                        .Add(Constants.QuantumSimulator)
                        .Add(Constants.ToffoliSimulator)
                        .Add(Constants.ResourcesEstimator)
                        .Add(EntryPoint.DefaultSimulator));
                    simulate.AddOption(option);
                });
            simulate.Handler = CommandHandler.Create<EntryPoint, string>(Simulate);

            var root = new RootCommand(EntryPoint.Summary) { simulate };
            foreach (var option in EntryPoint.Options) { root.AddGlobalOption(option); }

            // Set the simulate command as the default.
            foreach (var option in simulate.Options) { root.AddOption(option); }
            root.Handler = simulate.Handler;

            return await new CommandLineBuilder(root)
                .UseDefaults()
                .UseHelpBuilder(context => new QsHelpBuilder(context.Console))
                .Build()
                .InvokeAsync(args);
        }

        /// <summary>
        /// Simulates the entry point.
        /// </summary>
        /// <param name="entryPoint">The entry point.</param>
        /// <param name="simulator">The simulator to use.</param>
        /// <returns>The exit code.</returns>
        private static async Task<int> Simulate(EntryPoint entryPoint, string simulator)
        {
            simulator = DefaultIfShadowed(Constants.SimulatorOptions.First(), simulator, EntryPoint.DefaultSimulator);
            switch (simulator)
            {
                case Constants.ResourcesEstimator:
                    var resourcesEstimator = new ResourcesEstimator();
                    await entryPoint.Run(resourcesEstimator);
                    Console.WriteLine(resourcesEstimator.ToTSV());
                    break;
                default:
                    var (isCustom, createSimulator) = simulator switch
                    {
                        Constants.QuantumSimulator =>
                            (false, new Func<IOperationFactory>(() => new QuantumSimulator())),
                        Constants.ToffoliSimulator =>
                            (false, new Func<IOperationFactory>(() => new ToffoliSimulator())),
                        _ => (true, EntryPoint.CreateDefaultCustomSimulator)
                    };
                    if (isCustom && simulator != EntryPoint.DefaultSimulator)
                    {
                        DisplayCustomSimulatorError(simulator);
                        return 1;
                    }
                    await DisplayEntryPointResult(entryPoint, createSimulator);
                    break;
            }
            return 0;
        }

        /// <summary>
        /// Runs the entry point on a simulator and displays its return value.
        /// </summary>
        /// <param name="entryPoint">The entry point.</param>
        /// <param name="createSimulator">A function that creates an instance of the simulator to use.</param>
        private static async Task DisplayEntryPointResult(
            EntryPoint entryPoint, Func<IOperationFactory> createSimulator)
        {
            var simulator = createSimulator();
            try
            {
                var value = await entryPoint.Run(simulator);
#pragma warning disable CS0184
                if (!(value is QVoid))
#pragma warning restore CS0184
                {
                    Console.WriteLine(value);
                }
            }
            finally
            {
                if (simulator is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }

        /// <summary>
        /// Returns true if the alias is available for use by the driver (that is, the alias is not already used by an
        /// entry point option).
        /// </summary>
        /// <param name="alias">The alias to check.</param>
        /// <returns>True if the alias is available for use by the driver.</returns>
        private static bool IsAliasAvailable(string alias) =>
            !EntryPoint.Options.SelectMany(option => option.RawAliases).Contains(alias);

        /// <summary>
        /// Returns the default value and displays a warning if the alias is shadowed by an entry point option, and
        /// returns the original value otherwise.
        /// </summary>
        /// <typeparam name="T">The type of the values.</typeparam>
        /// <param name="alias">The primary option alias corresponding to the value.</param>
        /// <param name="value">The value of the option given on the command line.</param>
        /// <param name="defaultValue">The default value for the option.</param>
        /// <returns></returns>
        private static T DefaultIfShadowed<T>(string alias, T value, T defaultValue)
        {
            if (IsAliasAvailable(alias))
            {
                return value;
            }
            else
            {
                var originalForeground = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"Warning: Option {alias} is overriden by an entry point parameter name.");
                Console.Error.WriteLine($"         Using default value {defaultValue}.");
                Console.ForegroundColor = originalForeground;
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to create an option by removing aliases that are already in use by the entry point. If the first
        /// alias, which is considered the primary alias, is in use, then the option is not created.
        /// </summary>
        /// <typeparam name="T">The type of the option's argument.</typeparam>
        /// <param name="aliases">The option's aliases.</param>
        /// <param name="getDefaultValue">A function that returns the option's default value.</param>
        /// <param name="description">The option's description.</param>
        /// <returns>The result of trying to create the option.</returns>
        private static Result<Option<T>> TryCreateOption<T>(
                IEnumerable<string> aliases, Func<T> getDefaultValue, string description = null) =>
            IsAliasAvailable(aliases.First())
            ? Result<Option<T>>.Success(
                new Option<T>(aliases.Where(IsAliasAvailable).ToArray(), getDefaultValue, description))
            : Result<Option<T>>.Failure();

        /// <summary>
        /// Creates an argument parser that will use a default error message if parsing fails.
        /// </summary>
        /// <typeparam name="T">The type of the argument.</typeparam>
        /// <param name="parse">
        /// A function that takes the argument as a string and returns the parsed value and a boolean to indicate
        /// whether parsing succeeded.
        /// </param>
        /// <returns>An argument parser.</returns>
        private static ParseArgument<T> CreateArgumentParser<T>(Func<string, (bool, T)> parse) => result =>
        {
            var (success, value) = parse(result.Tokens.Single().Value);
            if (!success)
            {
                result.ErrorMessage = GetArgumentErrorMessage(
                    ((OptionResult)result.Parent).Token.Value, result.Tokens.Single().Value, typeof(T));
            }
            return value;
        };

        /// <summary>
        /// Parses a Q# range from an enumerable of strings, where the items are start and end or start, step, and end.
        /// </summary>
        /// <param name="option">The name of the option being parsed.</param>
        /// <param name="arg">The argument string for the option.</param>
        /// <param name="items">The items in the argument.</param>
        /// <returns>The result of parsing the strings.</returns>
        private static Result<QRange> ParseRangeFromEnumerable(string option, string arg, IEnumerable<string> items) =>
            items.Select(item => TryParseLong(option, item)).Sequence().Bind(values =>
                values.Count() == 2
                ? Result<QRange>.Success(new QRange(values.ElementAt(0), values.ElementAt(1)))
                : values.Count() == 3
                ? Result<QRange>.Success(new QRange(values.ElementAt(0), values.ElementAt(1), values.ElementAt(2)))
                : Result<QRange>.Failure(GetArgumentErrorMessage(option, arg, typeof(QRange))));

        /// <summary>
        /// Parses a long from a string.
        /// </summary>
        /// <param name="option">The name of the option being parsed.</param>
        /// <param name="str">The string to parse.</param>
        /// <returns>The result of parsing the string.</returns>
        private static Result<long> TryParseLong(string option, string str) =>
            long.TryParse(str, out var result)
            ? Result<long>.Success(result)
            : Result<long>.Failure(GetArgumentErrorMessage(option, str, typeof(long)));

        /// <summary>
        /// Returns an error message string for an argument parser.
        /// </summary>
        /// <param name="option">The name of the option.</param>
        /// <param name="arg">The value of the argument being parsed.</param>
        /// <param name="type">The expected type of the argument.</param>
        /// <returns>An error message string for an argument parser.</returns>
        private static string GetArgumentErrorMessage(string option, string arg, Type type) =>
            $"Cannot parse argument '{arg}' for option '{option}' as expected type {type}.";

        /// <summary>
        /// Displays an error message for using a non-default custom simulator.
        /// </summary>
        /// <param name="name">The name of the custom simulator.</param>
        private static void DisplayCustomSimulatorError(string name)
        {
            var originalForeground = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"The simulator '{name}' could not be found.");
            Console.ForegroundColor = originalForeground;
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"If '{name}' is a custom simulator, it must be set in the DefaultSimulator project property:");
            Console.Error.WriteLine();
            Console.Error.WriteLine("<PropertyGroup>");
            Console.Error.WriteLine($"  <DefaultSimulator>{name}</DefaultSimulator>");
            Console.Error.WriteLine("</PropertyGroup>");
        }
    }

    /// <summary>
    /// A modification of the command line <see cref="HelpBuilder"/> class.
    /// </summary>
    internal class QsHelpBuilder : HelpBuilder
    {
        public QsHelpBuilder(IConsole console) : base(console) { }

        protected override string ArgumentDescriptor(IArgument argument)
        {
            // Hide long argument descriptors.
            var descriptor = base.ArgumentDescriptor(argument);
            return descriptor.Length > 30 ? argument.Name : descriptor;
        }
    }

    /// <summary>
    /// The result of a process that can either succeed or fail.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    internal struct Result<T>
    {
        public bool IsSuccess { get; }
        public bool IsFailure { get => !IsSuccess; }
        public T Value { get => IsSuccess ? ValueOrDefault : throw new InvalidOperationException(); }
        public T ValueOrDefault { get; }
        public string ErrorMessage { get; }

        private Result(bool isSuccess, T value, string errorMessage)
        {
            IsSuccess = isSuccess;
            ValueOrDefault = value;
            ErrorMessage = errorMessage;
        }

        public static Result<T> Success(T value) => new Result<T>(true, value, default);

        public static Result<T> Failure(string errorMessage = null) => new Result<T>(false, default, errorMessage);
    }

    /// <summary>
    /// Extension methods for <see cref="Result{T}"/>.
    /// </summary>
    internal static class ResultExtensions
    {
        /// <summary>
        /// Sequentially composes two results, passing the value of the first result to another result-producing
        /// function if the first result is a success.
        /// </summary>
        /// <typeparam name="T">The type of the first result value.</typeparam>
        /// <typeparam name="U">The type of the second result value.</typeparam>
        /// <param name="result">The first result.</param>
        /// <param name="bind">A function that takes the value of the first result and returns a second result.</param>
        /// <returns>
        /// The first result if the first result is a failure; otherwise, the result of calling the bind function on the
        /// first result's value.
        /// </returns>
        internal static Result<U> Bind<T, U>(this Result<T> result, Func<T, Result<U>> bind) =>
            result.IsFailure ? Result<U>.Failure(result.ErrorMessage) : bind(result.Value);

        /// <summary>
        /// Converts an enumerable of results into a result of an enumerable.
        /// </summary>
        /// <typeparam name="T">The type of the result values.</typeparam>
        /// <param name="results">The results to sequence.</param>
        /// <returns>
        /// A result that contains an enumerable of the result values if all of the results are a success, or the first
        /// error message if one of the results is a failure.
        /// </returns>
        internal static Result<IEnumerable<T>> Sequence<T>(this IEnumerable<Result<T>> results) =>
            results.All(result => result.IsSuccess)
            ? Result<IEnumerable<T>>.Success(results.Select(results => results.Value))
            : Result<IEnumerable<T>>.Failure(results.First(results => results.IsFailure).ErrorMessage);

        /// <summary>
        /// Calls the action on the result value if the result is a success.
        /// </summary>
        /// <typeparam name="T">The type of the result value.</typeparam>
        /// <param name="result">The result.</param>
        /// <param name="onSuccess">The action to call if the result is a success.</param>
        internal static void Then<T>(this Result<T> result, Action<T> onSuccess)
        {
            if (result.IsSuccess)
            {
                onSuccess(result.Value);
            }
        }
    }
}