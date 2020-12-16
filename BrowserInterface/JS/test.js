document.getElementById("label").innerText = "Hello World"
setBindings()
var url = 'http://localhost:5000/server/'

//functions below
function setBindings()
{
    var extract_button = $('#extract')
    var file_browser = $('#file-input')
    var stats_button = $('#get_stats')
    var table = $('#image_table')
    
    file_browser.change(e => open_file_dialog(e))
    extract_button.click(extract_database)
    stats_button.click(get_stats)
}

function createHttpRequest(relative_url, http_verb, content = '', type = 'application/json') {

    var headers = {
        method: http_verb,
        headers: {
            'Content-Type': type,
        },
        body: content
    }

    return fetch(url + relative_url, headers)
}

async function extract_database() {
    var response = await fetch(url + "results", {
        method: 'GET',
        headers: {
            'Content-Type': 'application/json'
        }
    })
    var js_resp = await response.json()
    console.log(js_resp)
    
    clear_table()
    let i
    for (i = 0; i < js_resp.length; i++)
    {
        add_row(js_resp[i]['blob']['data'], js_resp[i]['class'])
    }
}

async function open_file_dialog(e)
{
    var file = e.target.files[0]
    ctx = document.getElementById('image_canvas').getContext('2d')
    var reader = new FileReader()
    reader.readAsDataURL(file)
    var img = new Image()
    img.onload = function () {
        ctx.drawImage(img, 0, 0, 224, 224)
    }
    reader.onload = async function (e) {
        img.src=reader.result
        
        var response = await fetch(url + 'single', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(reader.result.split(',')[1])
        })
        var js_resp = await response.json()
        document.getElementById("label").innerText = js_resp['class']
        console.log(js_resp)
    }
}

async function get_stats()
{
    var response = await fetch(url + 'dbstats', {
        method: 'GET',
        headers: {
            'Content-Type': 'application/json'
        }
    }).then(function(response) {
        return response.json()
    }).then(function (data) {
        var stats = data.split('\r\n')
        clear_class_table()
        let i
        for(i = 0; i < stats.length; i++)
        {
            add_class_row(stats[i])
        }
    })
}

function add_class_row(rowcontent)
{
    var content = rowcontent.split(': ')
    var classname = content[0]
    var amount = content[1]

    var row = document.createElement('tr')
    var row_class = document.createElement('td')
    var row_amount = document.createElement('td')
    var button = document.createElement('button')
    
    button.textContent = classname
    button.addEventListener('click', async function() {
        
        clear_table()
        var response = await fetch(url + 'id', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify(classname)
        })
        var js_resp = await response.json()
        console.log(js_resp)
        
        let i
        for (i = 0; i < js_resp.length; i++)
        {
            add_row(js_resp[i]['blob']['data'], js_resp[i]['class'])
        }
        
    })
    row_class.appendChild(button)
    row_amount.textContent = amount
    
    row.appendChild(row_class)
    row.appendChild(row_amount)
    
    var table = document.getElementById('class_table')
    table.appendChild(row)
    
}

function clear_class_table()
{
    var table = document.getElementById('class_table')
    while (table.getElementsByTagName('tr').length > 1)
    {
        table.deleteRow(1)
    }
}

function add_row(string64, classname) 
{
    var row = document.createElement('tr')
    var row_image = document.createElement('td')
    var row_class = document.createElement('td')
    var canvas = document.createElement('canvas')

    canvas.width = 224
    canvas.height = 224
    var ctx = canvas.getContext('2d')
    
    var img = new Image()
    img.onload = function () {
        ctx.drawImage(img, 0, 0, 224, 224)
    }
    img.src='data:image/jpg;base64, ' + string64
    row_class.textContent = classname
    row_image.appendChild(canvas)
    
    row.appendChild(row_image)
    row.appendChild(row_class)
    
    document.getElementById('image_table').appendChild(row)
}

function clear_table()
{
    var table = document.getElementById('image_table')
    while (table.getElementsByTagName('tr').length > 1)
    {
        table.deleteRow(1)
    }
}
