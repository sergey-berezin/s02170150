using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PredictorLibrary;

namespace WebLibrary.Controllers
{
    [ApiController]
    [Route("server")]
    public class ResultController : ControllerBase
    {
        private Predictor _predictor;

        [HttpGet("results")]
        public Result[] GetResults()
        {
            return Predictor.ExtractDatabase();
        }

        [HttpPost]
        public List<Result> ProcessDirectory([FromBody] string dir)
        {
            _predictor ??= new Predictor(dir, null);
            _predictor.Stop();
            _predictor.ImagePath = dir;
            
            _predictor.ProcessDirectory();

            var res = Predictor.GetDatabaseDir(dir);
            return res;
        }

        [HttpPost("single")]
        public Result ProcessSingleImage([FromBody] string base64string)
        {
            Console.WriteLine("Requested single image");
            
            _predictor ??= new Predictor("", null);
            _predictor.Stop();
            Result res = _predictor.SaveAndProcessImage(base64string);
            if (res == null)
            {
                Console.WriteLine("Null");
            }
            else
            {
                Console.WriteLine($"We got {res.ToString()}");
            }
            return res;
        }

        [HttpPost("id")]
        public Result[] ExtractByClass([FromBody] string classname)
        {
            return Predictor.ExtractByClass(classname);
        }
        
        [HttpGet("dbstats")]
        public string DBStats()
        {
            _predictor?.Stop();
            return JsonConvert.SerializeObject(Predictor.DatabaseStats());
        }

        [HttpDelete]
        public void ClearDatabase()
        {
            _predictor?.Stop();
            Predictor.ClearDatabase();
        }
    }
}
