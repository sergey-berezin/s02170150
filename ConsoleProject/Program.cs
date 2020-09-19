using System;
using System.Linq;
using PredictorLibrary;

namespace ConsoleProject
{
    class Program
    {
        static void Main(string[] args)
        {
            var a = new Predictor(args.FirstOrDefault() ?? "./images/");
            Console.WriteLine(a.ToString());
            a.process_image("E:/s02170150/images/dog.jpeg");
        }
    }
}
