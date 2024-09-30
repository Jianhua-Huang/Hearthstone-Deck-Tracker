#region

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using Hearthstone_Deck_Tracker.Controls.Error;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.LogReader.Handlers;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Hearthstone_Deck_Tracker.Windows;
using HearthWatcher;
using HearthWatcher.LogReader;
using static Hearthstone_Deck_Tracker.API.LogEvents;

#endregion

namespace Hearthstone_Deck_Tracker.LogReader
{
	public class LogWatcherManager
	{
		private readonly PowerHandler _powerLineHandler = new PowerHandler();
		private readonly GameInfoHandler _gameInfoHandler = new GameInfoHandler();
		private readonly ChoicesHandler _choicesHandler = new ChoicesHandler();
		private readonly ArenaHandler _arenaHandler = new ArenaHandler();
		private readonly LoadingScreenHandler _loadingScreenHandler = new LoadingScreenHandler();
		private HsGameState? _gameState;
		private GameV2? _game;
		private readonly LogWatcher _logWatcher;
		private bool _stop;

		public static LogWatcherInfo AchievementsLogWatcherInfo => new LogWatcherInfo { Name = "Achievements" };
		public static LogWatcherInfo PowerLogWatcherInfo => new LogWatcherInfo
		{
			Name = "Power",
			StartsWithFilters = new[] { "PowerTaskList.DebugPrintPower", "GameState.", "PowerProcessor.EndCurrentTaskList" },
			ContainsFilters = new[] { "Begin Spectating", "Start Spectator", "End Spectator" }
		};

		public static LogWatcherInfo ArenaLogWatcherInfo => new LogWatcherInfo { Name = "Arena" };
		public static LogWatcherInfo LoadingScreenLogWatcherInfo => new LogWatcherInfo { Name = "LoadingScreen", StartsWithFilters = new[] { "LoadingScreen.OnSceneLoaded", "Gameplay", "LoadingScreen.OnScenePreUnload", "MulliganManager.HandleGameStart" } };

		public LogWatcherManager()
		{
			_logWatcher = new LogWatcher(new[]
			{
				AchievementsLogWatcherInfo,
				PowerLogWatcherInfo,
				ArenaLogWatcherInfo,
				LoadingScreenLogWatcherInfo,
			});
			_logWatcher.OnNewLines += OnNewLines;
			_logWatcher.OnLogFileFound += OnLogFileFound;
			_logWatcher.OnLogLineIgnored += OnLogLineIgnored;

			_loadingScreenHandler.OnHearthMirrorCheckFailed += OnHearthMirroCheckFailed;
		}

		private void OnLogFileFound(string msg) => Log.Info(msg);
		private void OnLogLineIgnored(string msg) => Log.Warn(msg);

		private async void OnHearthMirroCheckFailed()
		{
			await Stop(true);
			Core.MainWindow.ActivateWindow();
			while(Core.MainWindow.Visibility != Visibility.Visible || Core.MainWindow.WindowState == WindowState.Minimized)
				await Task.Delay(100);
			await Core.MainWindow.ShowMessage("Uneven permissions",
				"It appears that Hearthstone (Battle.net) and HDT do not have the same permissions.\n\nPlease run both as administrator or local user.\n\nIf you don't know what any of this means, just run HDT as administrator.");
		}

		public async Task Start(GameV2 game)
		{
			if(!Helper.HearthstoneDirExists)
				await FindHearthstone();
			InitializeGameState(game);
			_stop = false;
			var logDirectory = Path.Combine(Config.Instance.HearthstoneDirectory, Config.Instance.HearthstoneLogsDirectoryName);
			Log.Info($"Using Hearthstone log directory '{logDirectory}'");
			_logWatcher.Start(logDirectory);
		}

		private async Task FindHearthstone()
		{
			Log.Warn("Hearthstone not found, waiting for process...");
			Process? proc;
			while((proc = User32.GetHearthstoneProc()) == null)
				await Task.Delay(500);
			var dir = new FileInfo(proc.MainModule.FileName).Directory?.FullName;
			if(dir == null)
			{
				const string msg = "Could not find Hearthstone installation";
				Log.Error(msg);
				ErrorManager.AddError(msg, "Please point HDT to your Hearthstone installation via 'options > tracker > settings > set hearthstone path'.");
				return;
			}
			Log.Info($"Found Hearthstone at '{dir}'");
			Config.Instance.HearthstoneDirectory = dir;
			Config.Save();
		}

		public async Task<bool> Stop(bool force = false)
		{
			_stop = true;
			return await _logWatcher.Stop(force);
		}

		private void InitializeGameState(GameV2 game)
		{
			_game = game;
			_gameState = new HsGameState(game) { GameHandler = new GameEventHandler(game) };
			_gameState.Reset();
		}

		private void OnNewLines(List<LogLine> lines)
		{
			if(_game == null || _gameState == null)
				return;
			foreach(var line in lines)
			{
				if(_stop)
					break;
				_game.GameTime.Time = line.Time;
				switch(line.Namespace)
				{
					case "Achievements":
						OnAchievementsLogLine.Execute(line.Line);
						break;
					case "Power":
						if(line.LineContent.StartsWith("GameState."))
						{
							_game.PowerLog.Add(line.Line);
							if(
								line.LineContent.StartsWith("GameState.DebugPrintEntityChoices") ||
								line.LineContent.StartsWith("GameState.DebugPrintEntitiesChosen")
							)
							{
								_choicesHandler.Handle(line.Line, _gameState, _game);
							}
							else
							{
								_choicesHandler.Flush(_gameState, _game);
							}

							if(line.LineContent.StartsWith("GameState.DebugPrintGame"))
							{
								_gameInfoHandler.Handle(line.Line, _gameState, _game);
							}
						}
						else if(line.LineContent.StartsWith("PowerProcessor.EndCurrentTaskList"))
						{
							//调试到此
							_choicesHandler.Handle(line.Line, _gameState, _game);
						}
						else
						{
							if(line.LineContent.Contains("致命之猫#515186"))
							{

							}
							_powerLineHandler.Handle(line.Line, _gameState, _game);
							OnPowerLogLine.Execute(line.Line);
							_choicesHandler.Flush(_gameState, _game);
						}
						break;
					case "Arena":
						_arenaHandler.Handle(line, _gameState, _game);
						OnArenaLogLine.Execute(line.Line);
						break;
					case "LoadingScreen":
						_loadingScreenHandler.Handle(line, _gameState, _game);
						break;
				}
			}
			//Helper.UpdateEverything(_game);
			if(lines.Count > 0)
			{
				var gameStateJson = _game.GetCurStateForGPT();
				var template = $@"你现在是一个非常厉害的炉石传说的玩家，你需要根据当前对局的状态进行决策。

决策规则：
1、绝对不能给出超出当前英雄剩余法力的命令结果；
2、在保障不会出现超法力的情况下，尽可能最大化利用剩余法力值；
3、最大化随从攻击机会：所有随从如果没有明显更优的解场需求，优先攻击对方英雄。非特殊情况下，所有随从都需要进行攻击；(随从的canAttack属性表示该随从是否可以进行攻击)；
4、如果有抽卡或其他随机性效果，先输出抽卡或随机效果指令，停止后续输出，我会执行你的操作，然后将随机操作后的更新结果再次传递给你；
5、若决定完全结束回合，明确输出END TURN；
6、每一个可操作对象，我都会给出对应的cardId，在你给出命令的时候，全部通过CardId进行操作；

输出命令格式如下：
1、**使用手牌命令：**
USE CARD {{{{card_id}}}} [ON {{{{target_card_id}}}}]
命令说明：
USE CARD:固定值
card_id:手牌的卡片ID
target_card_id:目标ID(这里的ID可以是本方英雄的ID)
[ON {{{{target_card_id}}}}]:中括号表示可选，当使用的手牌不需要目标的时候，不用给出ON

2、**使用随从攻击命令：**
MINION ATTACK {{{{attacker_id}}}} ON {{{{target_card_id}}}}
命令说明：
MINION ATTACK:固定值，表示使用随从进行攻击
attacker_id:随从的卡片ID
target_card_id:目标ID(这里的ID可以是对方英雄的ID)

3、**使用英雄技能**
USE HERO POWER

4、**结束回合命令：**
END TURN

对局的状态信息如下：
{gameStateJson}

根据以上信息，按照前面给出的""决策规则""，做出最佳的行动决策。每一行一个操作命令，并且仅给出对应的操作命令即可，我将逐行解析你的返回并调用执行器。";
			}
		}
	}
}
