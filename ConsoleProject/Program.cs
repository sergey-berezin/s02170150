using System;
using System.Linq;
using PredictorLibrary;

namespace ConsoleProject
{
    class Program
    {
        public static void Output(Result result)
        {
            Console.WriteLine(result);
        }

        static void Main(string[] args)
        {
            var a = new Predictor(args.FirstOrDefault() ?? "./images/", Output);
            Console.WriteLine(a.ToString());
            Console.CancelKeyPress += (sender, eArgs) => {
                a.Stop();
                eArgs.Cancel = true;
            };
            a.ProcessDirectory();
        }
    }
}
