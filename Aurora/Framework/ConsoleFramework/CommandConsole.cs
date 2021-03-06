/*
 * Copyright (c) Contributors, http://aurora-sim.org/, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using Aurora.Framework.Modules;
using Aurora.Framework.SceneInfo;
using Aurora.Framework.Utilities;
using Nini.Config;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Aurora.Framework.ConsoleFramework
{
    public class Commands
    {
        public static bool _ConsoleIsCaseSensitive = true;

        /// <value>
        ///     Commands organized by keyword in a tree
        /// </value>
        private readonly CommandSet tree = new CommandSet();

        /// <summary>
        ///     Get help for the given help string
        /// </summary>
        /// <param name="cmd">Parsed parts of the help string.  If empty then general help is returned.</param>
        /// <returns></returns>
        public List<string> GetHelp(string[] cmd)
        {
            return tree.GetHelp(new List<string>(0));
        }

        /// <summary>
        ///     Add a command to those which can be invoked from the console.
        /// </summary>
        /// <param name="command">The string that will make the command execute</param>
        /// <param name="commandHelp">The message that will show the user how to use the command</param>
        /// <param name="infomessage">Any information about how the command works or what it does</param>
        /// <param name="fn"></param>
        public void AddCommand(string command, string commandHelp, string infomessage, CommandDelegate fn)
        {
            CommandInfo info = new CommandInfo
                                   {
                                       command = command,
                                       commandHelp = commandHelp,
                                       info = infomessage,
                                       fn = new List<CommandDelegate> {fn}
                                   };
            tree.AddCommand(info);
        }

        public bool ContainsCommand(string command)
        {
            return tree.FindCommands(new string[1] {command}).Length > 0;
        }

        public string[] FindNextOption(string[] cmd)
        {
            return tree.FindCommands(cmd);
        }

        public string[] Resolve(string[] cmd)
        {
            return tree.ExecuteCommand(cmd);
        }

        #region Nested type: CommandInfo

        /// <summary>
        ///     Encapsulates a command that can be invoked from the console
        /// </summary>
        private class CommandInfo
        {
            /// <summary>
            ///     The command for this commandinfo
            /// </summary>
            public string command;

            /// <summary>
            ///     The help info for how to use this command
            /// </summary>
            public string commandHelp;

            /// <value>
            ///     The method to invoke for this command
            /// </value>
            public List<CommandDelegate> fn;

            /// <summary>
            ///     Any info about this command
            /// </summary>
            public string info;
        }

        #endregion

        #region Nested type: CommandSet

        private class CommandSet
        {
            private readonly Dictionary<string, CommandInfo> commands = new Dictionary<string, CommandInfo>();
            private readonly Dictionary<string, CommandSet> commandsets = new Dictionary<string, CommandSet>();
            public string Path = "";
            private bool m_allowSubSets = true;
            private string ourPath = "";

            public void Initialize(string path, bool allowSubSets)
            {
                m_allowSubSets = allowSubSets;
                ourPath = path;
                string[] paths = path.Split(' ');
                if (paths.Length != 0)
                {
                    Path = paths[paths.Length - 1];
                }
            }

            public void AddCommand(CommandInfo info)
            {
                if (!_ConsoleIsCaseSensitive) //Force to all lowercase
                {
                    info.command = info.command.ToLower();
                }

                //If our path is "", we can't replace, otherwise we just get ""
                string innerPath = info.command;
                if (ourPath != "")
                {
                    innerPath = info.command.Replace(ourPath, "");
                }
                if (innerPath.StartsWith(" "))
                {
                    innerPath = innerPath.Remove(0, 1);
                }
                string[] commandPath = innerPath.Split(new string[1] {" "}, StringSplitOptions.RemoveEmptyEntries);
                if (commandPath.Length == 1 || !m_allowSubSets)
                {
                    //Only one command after our path, its ours

                    //Add commands together if there is more than one event hooked to one command
                    if (commands.ContainsKey(info.command))
                    {
                        commands[info.command].fn.AddRange(info.fn);
                    }
                    else
                    {
                        commands[info.command] = info;
                    }
                }
                else
                {
                    //Its down the tree somewhere
                    CommandSet downTheTree;
                    if (!commandsets.TryGetValue(commandPath[0], out downTheTree))
                    {
                        //Need to add it to the tree then
                        downTheTree = new CommandSet();
                        downTheTree.Initialize((ourPath == "" ? "" : ourPath + " ") + commandPath[0], false);
                        commandsets.Add(commandPath[0], downTheTree);
                    }
                    downTheTree.AddCommand(info);
                }
            }

            public string[] ExecuteCommand(string[] commandPath)
            {
                if (commandPath.Length != 0)
                {
                    List<string> commandPathList = new List<string>(commandPath);
                    List<string> commandOptions = new List<string>();
                    int i;
                    for (i = commandPath.Length - 1; i >= 0; --i)
                    {
                        if (commandPath[i].Length > 1 && commandPath[i].Substring(0, 2) == "--")
                        {
                            commandOptions.Add(commandPath[i]);
                            commandPathList.RemoveAt(i);
                        }
                        else
                        {
                            break;
                        }
                    }
                    commandOptions.Reverse();
                    commandPath = commandPathList.ToArray();
                    if (commandOptions.Count > 0)
                        MainConsole.Instance.Info("Options: " + string.Join(", ", commandOptions.ToArray()));
                    List<string> cmdList;
                    if (commandPath.Length == 1 || !m_allowSubSets)
                    {
                        for (i = 1; i <= commandPath.Length; i++)
                        {
                            string[] comm = new string[i];
                            Array.Copy(commandPath, comm, i);
                            string com = string.Join(" ", comm);
                            //Only one command after our path, its ours
                            if (commands.ContainsKey(com))
                            {
                                MainConsole.Instance.HasProcessedCurrentCommand = false;

                                foreach (CommandDelegate fn in commands[com].fn.Where(fn => fn != null))
                                {
                                    cmdList = new List<string>(commandPath);
                                    cmdList.AddRange(commandOptions);
                                    fn(cmdList.ToArray());
                                }
                                return new string[0];
                            }
                            else if (commandPath[0] == "help")
                            {
                                List<string> help = GetHelp(commandOptions);

                                foreach (string s in help)
                                {
                                    MainConsole.Instance.Format(Level.Off, s);
                                }
                                return new string[0];
                            }
                            else
                            {
                                foreach (KeyValuePair<string, CommandInfo> cmd in commands)
                                {
                                    string[] cmdSplit = cmd.Key.Split(' ');
                                    if (cmdSplit.Length == commandPath.Length)
                                    {
                                        bool any = false;
                                        for (int k = 0; k < commandPath.Length; k++)
                                            if (!cmdSplit[k].StartsWith(commandPath[k]))
                                            {
                                                any = true;
                                                break;
                                            }
                                        bool same = !any;
                                        if (same)
                                        {
                                            foreach (CommandDelegate fn in cmd.Value.fn)
                                            {
                                                if (fn != null)
                                                {
                                                    fn(commandPath);
                                                }
                                            }
                                            return new string[0];
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        string cmdToExecute = commandPath[0];
                        if (cmdToExecute == "help")
                        {
                            cmdToExecute = commandPath[1];
                        }
                        if (!_ConsoleIsCaseSensitive)
                        {
                            cmdToExecute = cmdToExecute.ToLower();
                        }
                        //Its down the tree somewhere
                        CommandSet downTheTree;
                        if (commandsets.TryGetValue(cmdToExecute, out downTheTree))
                        {
                            cmdList = new List<string>(commandPath);
                            cmdList.AddRange(commandOptions);
                            return downTheTree.ExecuteCommand(cmdList.ToArray());
                        }
                        else
                        {
                            //See if this is part of a word, and if it is part of a word, execute it
                            foreach (
                                KeyValuePair<string, CommandSet> cmd in
                                    commandsets.Where(cmd => cmd.Key.StartsWith(commandPath[0])))
                            {
                                cmdList = new List<string>(commandPath);
                                cmdList.AddRange(commandOptions);
                                return cmd.Value.ExecuteCommand(cmdList.ToArray());
                            }

                            if (commands.ContainsKey(cmdToExecute))
                            {
                                foreach (CommandDelegate fn in commands[cmdToExecute].fn.Where(fn => fn != null))
                                {
                                    cmdList = new List<string>(commandPath);
                                    cmdList.AddRange(commandOptions);
                                    fn(cmdList.ToArray());
                                }
                                return new string[0];
                            }
                        }
                    }
                }

                return new string[0];
            }

            public string[] FindCommands(string[] command)
            {
                List<string> values = new List<string>();
                if (command.Length != 0)
                {
                    string innerPath = string.Join(" ", command);
                    if (!_ConsoleIsCaseSensitive)
                    {
                        innerPath = innerPath.ToLower();
                    }
                    if (ourPath != "")
                    {
                        innerPath = innerPath.Replace(ourPath, "");
                    }
                    if (innerPath.StartsWith(" "))
                    {
                        innerPath = innerPath.Remove(0, 1);
                    }
                    string[] commandPath = innerPath.Split(new string[1] {" "}, StringSplitOptions.RemoveEmptyEntries);
                    if ((commandPath.Length == 1 || !m_allowSubSets))
                    {
                        string fullcommand = string.Join(" ", command, 0, 2 > command.Length ? command.Length : 2);
                        values.AddRange(from cmd in commands
                                        where cmd.Key.StartsWith(fullcommand)
                                        select cmd.Value.commandHelp);
                        if (commandPath.Length != 0)
                        {
                            string cmdToExecute = commandPath[0];
                            if (cmdToExecute == "help")
                            {
                                cmdToExecute = commandPath[1];
                            }
                            if (!_ConsoleIsCaseSensitive)
                            {
                                cmdToExecute = cmdToExecute.ToLower();
                            }
                            CommandSet downTheTree;
                            if (commandsets.TryGetValue(cmdToExecute, out downTheTree))
                            {
                                values.AddRange(downTheTree.FindCommands(commandPath));
                            }
                            else
                            {
                                //See if this is part of a word, and if it is part of a word, execute it
                                foreach (
                                    KeyValuePair<string, CommandSet> cmd in
                                        commandsets.Where(cmd => cmd.Key.StartsWith(cmdToExecute)))
                                {
                                    values.AddRange(cmd.Value.FindCommands(commandPath));
                                }
                            }
                        }
                    }
                    else if (commandPath.Length != 0)
                    {
                        string cmdToExecute = commandPath[0];
                        if (cmdToExecute == "help")
                        {
                            cmdToExecute = commandPath[1];
                        }
                        if (!_ConsoleIsCaseSensitive)
                        {
                            cmdToExecute = cmdToExecute.ToLower();
                        }
                        //Its down the tree somewhere
                        CommandSet downTheTree;
                        if (commandsets.TryGetValue(cmdToExecute, out downTheTree))
                        {
                            return downTheTree.FindCommands(commandPath);
                        }
                        else
                        {
                            //See if this is part of a word, and if it is part of a word, execute it
                            foreach (
                                KeyValuePair<string, CommandSet> cmd in
                                    commandsets.Where(cmd => cmd.Key.StartsWith(cmdToExecute)))
                            {
                                return cmd.Value.FindCommands(commandPath);
                            }
                        }
                    }
                }

                return values.ToArray();
            }

            public List<string> GetHelp(List<string> options)
            {
                MainConsole.Instance.Info("HTML mode: " + options.Contains("--html"));
                List<string> help = new List<string>();
                if (commandsets.Count != 0)
                {
                    help.Add("");
                    help.Add("------- Help Sets (type the name and help to get more info about that set) -------");
                    help.Add("");
                }
                List<string> paths = new List<string>();

                paths.AddRange(commandsets.Values.Select(set => string.Format("-- Help Set: {0}", set.Path)));

                help.AddRange(StringUtils.AlphanumericSort(paths));
                if (help.Count != 0)
                {
                    help.Add("");
                    help.Add("------- Help options -------");
                    help.Add("");
                }
                paths.Clear();

				paths.AddRange(
                    commands.Values.Select(
                        command =>
                        string.Format("-- {0}  [{1}]:   {2}", command.command, command.commandHelp, command.info)));

                help.AddRange(StringUtils.AlphanumericSort(paths));
                return help;
            }
        }

        #endregion
    }

    public delegate void CommandDelegate(string[] cmd);

    public class Parser
    {
        public static string[] Parse(string text)
        {
            List<string> result = new List<string>();

            int index;
            int startingIndex = -1;
            string[] unquoted = text.Split(new[] {'"'});

            for (index = 0; index < unquoted.Length; index++)
            {
                if (unquoted[index].StartsWith("/") || startingIndex >= 0)
                {
                    startingIndex = index;
                    if (unquoted[index] != "")
                        result.Add(unquoted[index]);
                }
                else
                {
                    startingIndex = 0;
                    string[] words = unquoted[index].Split(new[] {' '});
                    result.AddRange(words.Where(w => w != String.Empty));
                }
            }

            return result.ToArray();
        }
    }

    /// <summary>
    ///     A console that processes commands internally
    /// </summary>
    public class CommandConsole : ICommandConsole, IDisposable
    {
        public bool m_isPrompting;
        public int m_lastSetPromptOption;
        protected System.IO.TextWriter m_logFile;
        public List<string> m_promptOptions = new List<string>();

        public virtual void Initialize(IConfigSource source, ISimulationBase baseOpenSim)
        {
            if (source.Configs["Console"] == null ||
                source.Configs["Console"].GetString("Console", String.Empty) != Name)
            {
                return;
            }

            baseOpenSim.ApplicationRegistry.RegisterModuleInterface<ICommandConsole>(this);
            MainConsole.Instance = this;

            m_Commands.AddCommand("help", "help", "Get a general command list", Help);
            string logName = "";
            if (source.Configs["Console"] != null)
                logName = source.Configs["Console"].GetString("LogAppendName", "");
            InitializeLog(logName);
        }

        protected void InitializeLog(string filename)
        {
            m_logFile = StreamWriter.Synchronized(new StreamWriter(System.IO.Path.GetFileNameWithoutExtension(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName) + filename + ".log", true));
        }

        public void Dispose()
        {
            m_logFile.Close();
        }

        public void Help(string[] cmd)
        {
            List<string> help = m_Commands.GetHelp(cmd);

            foreach (string s in help)
                Output(s, Level.Off);
        }

        /// <summary>
        ///     Display a command prompt on the console and wait for user input
        /// </summary>
        public void Prompt()
        {
            // Set this culture for the thread 
            // to en-US to avoid number parsing issues
            Culture.SetCurrentCulture();
            string line = ReadLine(m_defaultPrompt + "# ", true, true);

            if (line != String.Empty && line.Replace(" ", "") != String.Empty) //If there is a space, its fine
            {
                MainConsole.Instance.Info("[CONSOLE] Invalid command");
            }
        }

        public void RunCommand(string cmd)
        {
            string[] parts = Parser.Parse(cmd);
            m_Commands.Resolve(parts);
            Output("", Threshold);
        }

        public virtual string ReadLine(string p, bool isCommand, bool e)
        {
            string oldDefaultPrompt = m_defaultPrompt;
            m_defaultPrompt = p;
            Console.Write("{0}", p);
            string cmdinput = Console.ReadLine();

            if (isCommand)
            {
                string[] cmd = m_Commands.Resolve(Parser.Parse(cmdinput));

                if (cmd.Length != 0)
                {
                    int i;

                    for (i = 0; i < cmd.Length; i++)
                    {
                        if (cmd[i].Contains(" "))
                            cmd[i] = "\"" + cmd[i] + "\"";
                    }
                }
                m_defaultPrompt = oldDefaultPrompt;
                return String.Empty;
            }
            return cmdinput;
        }

        public string Prompt(string prompt)
        {
            return Prompt(prompt, "");
        }

        public string Prompt(string prompt, string defaultResponse)
        {
            return Prompt(prompt, defaultResponse, new List<string>());
        }

        public string Prompt(string prompt, string defaultResponse, List<char> excludedCharacters)
        {
            return Prompt(prompt, defaultResponse, new List<string>(), excludedCharacters);
        }

        public string Prompt(string prompt, string defaultresponse, List<string> options)
        {
            return Prompt(prompt, defaultresponse, options, new List<char>());
        }

        // Displays a command prompt and returns a default value, user may only enter 1 of 2 options
        public string Prompt(string prompt, string defaultresponse, List<string> options, List<char> excludedCharacters)
        {
            m_isPrompting = true;
            m_promptOptions = new List<string>(options);

            bool itisdone = false;
            string optstr = options.Aggregate(String.Empty, (current, s) => current + (" " + s));
            string temp = InternalPrompt(prompt, defaultresponse, options);
 
			while (!itisdone && options.Count > 0)
            {
                if (options.Contains(temp))
                {
                    itisdone = true;
                }
                else
                {
                    Console.WriteLine("Valid options are" + optstr);
                    temp = InternalPrompt(prompt, defaultresponse, options);
                }
            }
            itisdone = false;
            while (!itisdone && excludedCharacters.Count > 0)
            {
                foreach (char c in excludedCharacters.Where(c => temp.Contains(c.ToString())))
                {
                    Console.WriteLine("The character \"" + c.ToString() + "\" is not permitted.");
                    itisdone = false;
                }
            }
            m_isPrompting = false;
            m_promptOptions.Clear();
            return temp;
        }

        private string InternalPrompt(string prompt, string defaultresponse, List<string> options)
        {
            string ret = ReadLine(String.Format("{0}{2} [{1}]: ",
                                                prompt,
                                                defaultresponse,
                                                options.Count == 0
                                                    ? ""
                                                    : ", Options are [" + string.Join(", ", options.ToArray()) + "]"
                                      ), false, true);
            if (ret == String.Empty)
                ret = defaultresponse;

            return ret;
        }

        // Displays a prompt and waits for the user to enter a string, then returns that string
        // (Done with no echo and suitable for passwords)
        public string PasswordPrompt(string p)
        {
            m_isPrompting = true;
            string line = ReadLine(p + ": ", false, false);
            m_isPrompting = false;
            return line;
        }

        public virtual void Output(string text, Level level)
        {
            if (Threshold <= level)
            {
                MainConsole.TriggerLog(level.ToString(), text);
                text = string.Format("{0}:{1}:{2}: {3}",
                    (DateTime.Now.Hour < 10 ? "0" + DateTime.Now.Hour : DateTime.Now.Hour.ToString()),
                    (DateTime.Now.Minute < 10 ? "0" + DateTime.Now.Minute : DateTime.Now.Minute.ToString()),
                    (DateTime.Now.Second < 10 ? "0" + DateTime.Now.Second : DateTime.Now.Second.ToString()), text);

                Console.WriteLine(text);
                if (m_logFile != null)
                {
                    m_logFile.WriteLine(text);
                    m_logFile.Flush();
                }
            }
        }

        public virtual void LockOutput()
        {
        }

        public virtual void UnlockOutput()
        {
        }

        public virtual bool CompareLogLevels(string a, string b)
        {
            Level aa = (Level)Enum.Parse(typeof(Level), a, true);
            Level bb = (Level)Enum.Parse(typeof(Level), b, true);
            return aa <= bb;
        }

        /// <summary>
        ///     The default prompt text.
        /// </summary>
        public virtual string DefaultPrompt
        {
            set { m_defaultPrompt = value; }
            get { return m_defaultPrompt; }
        }

        protected string m_defaultPrompt;

        public virtual string Name
        {
            get { return "CommandConsole"; }
        }

        public Commands m_Commands = new Commands();

        public Commands Commands
        {
            get { return m_Commands; }
            set { m_Commands = value; }
        }

        public IScene ConsoleScene
        {
            get { return m_ConsoleScene; }
            set { m_ConsoleScene = value; }
        }

        public bool HasProcessedCurrentCommand { get; set; }

        public IScene m_ConsoleScene;

        /// <summary>
        ///     Starts the prompt for the console. This will never stop until the region is closed.
        /// </summary>
        public void ReadConsole()
        {
            while (true)
            {
                Prompt();
            }
        }

        public Level Threshold { get; set; }

        #region ILog Members

        public bool IsDebugEnabled
        {
            get { return Threshold <= Level.Debug; }
        }

        public bool IsErrorEnabled
        {
            get { return Threshold <= Level.Error; }
        }

        public bool IsFatalEnabled
        {
            get { return Threshold <= Level.Fatal; }
        }

        public bool IsInfoEnabled
        {
            get { return Threshold <= Level.Info; }
        }

        public bool IsWarnEnabled
        {
            get { return Threshold <= Level.Warn; }
        }

        public bool IsTraceEnabled
        {
            get { return Threshold <= Level.Trace; }
        }

        public void Debug(object message)
        {
            Output(message.ToString(), Level.Debug);
        }

        public void DebugFormat(string format, params object[] args)
        {
            Output(string.Format(format, args), Level.Debug);
        }

        public void Error(object message)
        {
            Output(message.ToString(), Level.Error);
        }

        public void ErrorFormat(string format, params object[] args)
        {
            Output(string.Format(format, args), Level.Error);
        }

        public void Fatal(object message)
        {
            Output(message.ToString(), Level.Fatal);
        }

        public void FatalFormat(string format, params object[] args)
        {
            Output(string.Format(format, args), Level.Fatal);
        }

        public void Format(Level level, string format, params object[] args)
        {
            Output(string.Format(format, args), level);
        }

        public void Info(object message)
        {
            Output(message.ToString(), Level.Info);
        }

        public void InfoFormat(string format, params object[] args)
        {
            Output(string.Format(format, args), Level.Info);
        }

        public void Log(Level level, object message)
        {
            Output(message.ToString(), level);
        }

        public void Trace(object message)
        {
            Output(message.ToString(), Level.Trace);
        }

        public void TraceFormat(string format, params object[] args)
        {
            Output(string.Format(format, args), Level.Trace);
        }

        public void Warn(object message)
        {
            Output(message.ToString(), Level.Warn);
        }

        public void WarnFormat(string format, params object[] args)
        {
            Output(string.Format(format, args), Level.Warn);
        }

        #endregion
    }
}