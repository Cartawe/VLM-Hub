# VLMHub

Aplicación de consola .NET 8 para seleccionar un origen documental desde un servidor MySQL.

## Flujo actual

1. Solicita servidor/IP, usuario y contraseña MySQL.
2. Intenta conectarse y obtiene las bases de datos visibles para ese usuario.
3. Permite seleccionar una base de datos por índice o nombre.
4. Obtiene y muestra sus tablas físicas.
5. Permite seleccionar una tabla por índice o nombre.
6. Obtiene y muestra sus columnas, tipo, nulabilidad y pertenencia a la PK.
7. Permite seleccionar la columna documental por índice o nombre.
8. Las columnas de la clave primaria se muestran, pero no pueden seleccionarse como columna documental.

Los errores de conexión o consulta se capturan en el flujo de consola. VLMHub informa el problema y vuelve al paso correspondiente sin finalizar el proceso.

## Estructura

```text
VlmHub.Console/
├── Program.cs
├── App/
│   └── VlmHubConsoleApp.cs
└── Views/
    └── ConsoleUi.cs

VlmHub.MySQL/
├── MySqlServer.cs
└── Models/
    └── ColumnInfo.cs
```

### `VlmHub.Console`

Responsable de la interacción con el usuario y de orquestar los pasos de la aplicación.

- `Program.cs`: punto de entrada y última barrera de manejo de errores.
- `VlmHubConsoleApp.cs`: controla el orden conexión → base de datos → tabla → columna.
- `ConsoleUi.cs`: concentra toda la presentación con Spectre.Console.

### `VlmHub.MySQL`

Responsable exclusivamente del acceso al servidor MySQL.

- `MySqlServer.cs`: configuración de conexión y consultas de catálogo.
- `ColumnInfo.cs`: información estructural de una columna.

## Ejecución

Desde la raíz de la solución:

```bash
dotnet restore
dotnet build VlmHub.sln
dotnet run --project VlmHub.Console
```

La aplicación debe ejecutarse en una terminal interactiva porque Spectre.Console necesita leer la entrada del teclado.
