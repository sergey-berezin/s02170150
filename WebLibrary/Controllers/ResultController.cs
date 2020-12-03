using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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

        [HttpGet("dbstats")]
        public string DBStats()
        {
            return Predictor.DatabaseStats();
        }

        [HttpDelete]
        public void ClearDatabase()
        {
            _predictor?.Stop();
            Predictor.ClearDatabase();
        }
    }
}
