using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.LogReader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace powerAnalize
{
	internal class Program
	{
		[STAThread]
		static void Main(string[] args)
		{
			var logManager = new LogWatcherManager();
			var game = new GameV2();
			logManager.Start(game).Wait();

			Console.ReadLine();
		}
	}
}
