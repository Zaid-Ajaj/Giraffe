﻿module SampleApp.App

open System
open System.IO
open System.Security.Claims
open System.Collections.Generic
open System.Threading
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Giraffe.Tasks
open Giraffe.HttpContextExtensions
open Giraffe.HttpHandlers
open Giraffe.Middleware
open Giraffe.Razor.HttpHandlers
open Giraffe.Razor.Middleware
open SampleApp.Models
open SampleApp.HtmlViews

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> setStatusCode 500 >=> text ex.Message

// ---------------------------------
// Web app
// ---------------------------------

let authScheme = "Cookie"

let accessDenied = setStatusCode 401 >=> text "Access Denied"

let mustBeUser = requiresAuthentication accessDenied

let mustBeAdmin =
    requiresAuthentication accessDenied
    >=> requiresRole "Admin" accessDenied

let loginHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let issuer = "http://localhost:5000"
            let claims =
                [
                    Claim(ClaimTypes.Name,      "John",  ClaimValueTypes.String, issuer)
                    Claim(ClaimTypes.Surname,   "Doe",   ClaimValueTypes.String, issuer)
                    Claim(ClaimTypes.Role,      "Admin", ClaimValueTypes.String, issuer)
                ]
            let identity = ClaimsIdentity(claims, authScheme)
            let user     = ClaimsPrincipal(identity)

            do! ctx.Authentication.SignInAsync(authScheme, user)

            return! text "Successfully logged in" next ctx
        }

let userHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        text ctx.User.Identity.Name next ctx

let showUserHandler id =
    mustBeAdmin >=>
    text (sprintf "User ID: %i" id)

let time() = System.DateTime.Now.ToString()

[<CLIMutable>]
type Car =
    {
        Name   : string
        Make   : string
        Wheels : int
        Built  : DateTime
    }

let submitCar =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let! car = ctx.BindModel<Car>()
            return! json car next ctx
        }

let smallFileUploadHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            return!
                (match ctx.Request.HasFormContentType with
                | false -> setStatusCode 400 >=> text "Bad request"
                | true  ->
                    ctx.Request.Form.Files
                    |> Seq.fold (fun acc file -> sprintf "%s\n%s" acc file.FileName) ""
                    |> text) next ctx
        }

let largeFileUploadHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let formFeature = ctx.Features.Get<IFormFeature>()
            let! form = formFeature.ReadFormAsync CancellationToken.None
            return!
                (form.Files
                |> Seq.fold (fun acc file -> sprintf "%s\n%s" acc file.FileName) ""
                |> text) next ctx
        }

let webApp =
    choose [
        GET >=>
            choose [
                route  "/"           >=> text "index"
                route  "/ping"       >=> text "pong"
                route  "/error"      >=> (fun _ _ -> failwith "Something went wrong!")
                route  "/login"      >=> loginHandler
                route  "/logout"     >=> signOff authScheme >=> text "Successfully logged out."
                route  "/user"       >=> mustBeUser >=> userHandler
                routef "/user/%i"    showUserHandler
                route  "/razor"      >=> razorHtmlView "Person" { Name = "Razor" }
                route  "/razorHello" >=> razorHtmlView "Hello" ""
                route  "/fileupload" >=> razorHtmlView "FileUpload" ""
                route  "/person"     >=> (personView { Name = "Html Node" } |> renderHtml)
                route  "/once"       >=> (time() |> text)
                route  "/everytime"  >=> warbler (fun _ -> (time() |> text))
            ]
        POST >=>
            choose [
                route "/small-upload" >=> smallFileUploadHandler
                route "/large-upload" >=> largeFileUploadHandler ]
        route "/car" >=> submitCar
        setStatusCode 404 >=> text "Not Found" ]

// ---------------------------------
// Main
// ---------------------------------

let cookieAuth =
    new CookieAuthenticationOptions(
            AuthenticationScheme    = authScheme,
            AutomaticAuthenticate   = true,
            AutomaticChallenge      = false,
            CookieHttpOnly          = true,
            CookieSecure            = CookieSecurePolicy.SameAsRequest,
            SlidingExpiration       = true,
            ExpireTimeSpan          = TimeSpan.FromDays 7.0
    )

let configureApp (app : IApplicationBuilder) =
    app.UseGiraffeErrorHandler errorHandler
    app.UseCookieAuthentication cookieAuth |> ignore
    app.UseStaticFiles() |> ignore
    app.UseGiraffe webApp

let configureServices (services : IServiceCollection) =
    let sp  = services.BuildServiceProvider()
    let env = sp.GetService<IHostingEnvironment>()
    let viewsFolderPath = Path.Combine(env.ContentRootPath, "Views")

    services.AddAuthentication() |> ignore
    services.AddDataProtection() |> ignore
    services.AddRazorEngine viewsFolderPath |> ignore

let configureLogging (loggerFactory : ILoggerFactory) =
    loggerFactory.AddConsole(LogLevel.Error).AddDebug() |> ignore

[<EntryPoint>]
let main argv =
    let contentRoot = Directory.GetCurrentDirectory()
    let webRoot     = Path.Combine(contentRoot, "WebRoot")
    WebHostBuilder()
        .UseKestrel()
        .UseContentRoot(contentRoot)
        .UseWebRoot(webRoot)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(Action<IServiceCollection> configureServices)
        .ConfigureLogging(Action<ILoggerFactory> configureLogging)
        .Build()
        .Run()
    0