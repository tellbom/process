using FlowableWrapper.Api.Filters;
using FlowableWrapper.Application.Services;
using FlowableWrapper.Application.Slots;
using FlowableWrapper.Configuration;
using FlowableWrapper.Domain.Abstractions;
using FlowableWrapper.Domain.ElasticSearch;
using FlowableWrapper.Domain.Flowable;
using FlowableWrapper.Domain.Services;
using FlowableWrapper.Infrastructure.CurrentUser;
using FlowableWrapper.Infrastructure.ElasticSearch;
using FlowableWrapper.Infrastructure.Flowable;
using FlowableWrapper.Infrastructure.Slots;

var builder = WebApplication.CreateBuilder(args);

// ═══════════════════════════════════════════════════════════════
// 配置绑定（IOptions<T> 模式）
// ═══════════════════════════════════════════════════════════════
builder.Services.Configure<ElasticSearchOptions>(
    builder.Configuration.GetSection("ElasticSearch"));

builder.Services.Configure<FlowableOptions>(
    builder.Configuration.GetSection("Flowable"));

builder.Services.Configure<BusinessTypeProcessMapping>(
    builder.Configuration.GetSection("BusinessTypeProcessMapping"));

// ═══════════════════════════════════════════════════════════════
// 基础设施：当前用户
// ═══════════════════════════════════════════════════════════════
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

// ═══════════════════════════════════════════════════════════════
// 基础设施：Flowable HTTP 客户端
// ═══════════════════════════════════════════════════════════════
// 使用 IHttpClientFactory 管理 HttpClient 生命周期
builder.Services.AddHttpClient<FlowableHttpClient>();

// Flowable 各 Service 实现（Scoped，跟随请求生命周期）
builder.Services.AddScoped<IFlowableRuntimeService, FlowableRuntimeServiceImpl>();
builder.Services.AddScoped<IFlowableTaskService, FlowableTaskServiceImpl>();
builder.Services.AddScoped<IFlowableHistoryService, FlowableHistoryServiceImpl>();
builder.Services.AddScoped<IFlowableRepositoryService, FlowableRepositoryServiceImpl>();

// ═══════════════════════════════════════════════════════════════
// 基础设施：Elasticsearch
// ═══════════════════════════════════════════════════════════════
builder.Services.AddSingleton<IElasticSearchService, ElasticSearchServiceImpl>();

// ═══════════════════════════════════════════════════════════════
// 应用服务（Phase 3-9 逐步注册，此处预留占位注释）
// ═══════════════════════════════════════════════════════════════

// Phase 3 — Slot 模型
builder.Services.AddScoped<SlotVariableConverter>();
builder.Services.AddScoped<IProcessSlotConfigProvider, ElasticSearchSlotConfigProvider>();

// Phase 5 — 任务执行
builder.Services.AddScoped<TaskExecutionAppService>();

// Phase 6 — 流程生命周期
builder.Services.AddScoped<ProcessLifecycleAppService>();

// Phase 7 — BPMN 部署
builder.Services.AddScoped<BpmnDeploymentAppService>();

// Phase 8 — 回调
// 1. 注册 ProcessCallbackAppService
builder.Services.AddScoped<ProcessCallbackAppService>();

// 2. 注册具名 HttpClient（用于回调业务系统）
builder.Services.AddHttpClient("BusinessCallback");
//
// 说明：
//   ProcessCallbackAppService 通过 IHttpClientFactory.CreateClient("BusinessCallback")
//   获取 HttpClient 实例，由 IHttpClientFactory 统一管理连接池
//   避免直接 new HttpClient() 造成 Socket 耗尽
//
// 如果业务系统回调需要公共配置（如基础 URL、超时、重试策略），
// 可以在此处统一配置，例如：
//
builder.Services.AddHttpClient("BusinessCallback", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
//
// 如果后续引入 Polly 做 HTTP 重试（可选，当前由 Flowable 重试机制兜底），
// 可以在此处添加：
// .AddTransientHttpErrorPolicy(p => p.RetryAsync(3));

// Phase 9 — 查询
builder.Services.AddScoped<ProcessQueryAppService>();

// Phase 10 — 流程图渲染
builder.Services.AddScoped<ProcessFlowRenderAppService>();

// ═══════════════════════════════════════════════════════════════
// MVC + 全局异常过滤器
// ═══════════════════════════════════════════════════════════════
builder.Services.AddControllers(options =>
{
    options.Filters.Add<GlobalExceptionFilter>();
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "流程中心 API", Version = "v1" });
    // TODO: Phase N Keycloak JWT 接入后在此添加 Bearer 认证配置
});

// CORS（开发阶段全开，生产环境按需限制）
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// ═══════════════════════════════════════════════════════════════
// 中间件管道
// ═══════════════════════════════════════════════════════════════
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseRouting();

// TODO: Phase N — Keycloak JWT 接入后加入
// app.UseAuthentication();
// app.UseAuthorization();

app.MapControllers();

app.Run();
