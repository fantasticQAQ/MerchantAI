using MerchantAdmin.AI.API.Ai.Tools;

namespace MerchantAdmin.AI.API.Tests;

/// <summary>
/// 精简版 swagger 文档（形状与真实 Swashbuckle 输出一致：$ref、requestBody、query 参数、nullable）。
/// 测试用它而不是去打真实的 :5002，保证测试是自洽的、随时可跑的。
/// </summary>
public static class SwaggerFixture
{
    public const string Json = """
    {
      "openapi": "3.0.4",
      "paths": {
        "/api/Products": {
          "get": {
            "tags": ["Products"],
            "parameters": [
              { "name": "name", "in": "query", "schema": { "type": "string", "nullable": true } },
              { "name": "page", "in": "query", "schema": { "type": "integer", "format": "int32", "default": 1 } },
              { "name": "pageSize", "in": "query", "schema": { "type": "integer", "format": "int32", "default": 10 } }
            ],
            "responses": { "200": { "description": "OK" } }
          },
          "post": {
            "tags": ["Products"],
            "requestBody": {
              "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CreateProductCommand" } } }
            },
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Products/{productId}": {
          "delete": {
            "tags": ["Products"],
            "parameters": [
              { "name": "productId", "in": "path", "required": true, "schema": { "type": "integer", "format": "int32" } }
            ],
            "responses": { "200": { "description": "OK" } }
          },
          "put": {
            "tags": ["Products"],
            "parameters": [
              { "name": "productId", "in": "path", "required": true, "schema": { "type": "integer", "format": "int32" } }
            ],
            "requestBody": {
              "content": { "application/json": { "schema": { "$ref": "#/components/schemas/UpdateProductCommand" } } }
            },
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Orders": {
          "get": {
            "tags": ["Orders"],
            "parameters": [
              { "name": "orderId", "in": "query", "schema": { "type": "integer", "format": "int32", "nullable": true } },
              { "name": "status", "in": "query", "schema": { "type": "string", "nullable": true } },
              { "name": "page", "in": "query", "schema": { "type": "integer", "format": "int32", "default": 1 } },
              { "name": "pageSize", "in": "query", "schema": { "type": "integer", "format": "int32", "default": 10 } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Orders/create": {
          "post": {
            "tags": ["Orders"],
            "requestBody": {
              "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CreateOrderCommand" } } }
            },
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Orders/{orderId}/cancel": {
          "post": {
            "tags": ["Orders"],
            "parameters": [
              { "name": "orderId", "in": "path", "required": true, "schema": { "type": "integer", "format": "int32" } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Orders/{orderId}/pay": {
          "post": {
            "tags": ["Orders"],
            "parameters": [
              { "name": "orderId", "in": "path", "required": true, "schema": { "type": "integer", "format": "int32" } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Orders/{orderId}/refund": {
          "post": {
            "tags": ["Orders"],
            "parameters": [
              { "name": "orderId", "in": "path", "required": true, "schema": { "type": "integer", "format": "int32" } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Orders/{orderId}": {
          "delete": {
            "tags": ["Orders"],
            "parameters": [
              { "name": "orderId", "in": "path", "required": true, "schema": { "type": "integer", "format": "int32" } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Dashboard": {
          "get": { "tags": ["Dashboard"], "responses": { "200": { "description": "OK" } } }
        },
        "/api/Orders/export": {
          "get": {
            "tags": ["Orders"],
            "parameters": [
              { "name": "status", "in": "query", "schema": { "type": "string", "nullable": true } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Test/setRedisKey": {
          "post": {
            "tags": ["Test"],
            "parameters": [
              { "name": "key", "in": "query", "schema": { "type": "string", "nullable": true } },
              { "name": "value", "in": "query", "schema": { "type": "string", "nullable": true } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        },
        "/api/Logs": {
          "get": {
            "tags": ["Logs"],
            "parameters": [
              { "name": "page", "in": "query", "schema": { "type": "integer", "format": "int32", "default": 1 } },
              { "name": "pageSize", "in": "query", "schema": { "type": "integer", "format": "int32", "default": 10 } }
            ],
            "responses": { "200": { "description": "OK" } }
          }
        }
      },
      "components": {
        "schemas": {
          "CreateOrderCommand": {
            "type": "object",
            "properties": {
              "orderItems": { "type": "array", "nullable": true, "items": { "$ref": "#/components/schemas/OrderItemDto" } }
            },
            "additionalProperties": false
          },
          "OrderItemDto": {
            "type": "object",
            "properties": {
              "productId": { "type": "integer", "format": "int32" },
              "productName": { "type": "string", "nullable": true },
              "price": { "type": "number", "format": "double" },
              "quantity": { "type": "number", "format": "double" }
            },
            "additionalProperties": false
          },
          "CreateProductCommand": {
            "type": "object",
            "properties": {
              "name": { "type": "string", "nullable": true },
              "price": { "type": "number", "format": "double" },
              "stock": { "type": "number", "format": "double" },
              "isActive": { "type": "boolean" }
            },
            "additionalProperties": false
          },
          "UpdateProductCommand": {
            "type": "object",
            "properties": {
              "productId": { "type": "integer", "format": "int32" },
              "name": { "type": "string", "nullable": true },
              "price": { "type": "number", "format": "double", "nullable": true },
              "stockDelta": { "type": "number", "format": "double", "nullable": true },
              "isActive": { "type": "boolean", "nullable": true }
            },
            "additionalProperties": false
          }
        }
      }
    }
    """;

    private static readonly Lazy<IReadOnlyDictionary<string, OpenApiOperation>> LazyOperations =
        new(() => OpenApiToolSource.Parse(Json));

    public static IReadOnlyDictionary<string, OpenApiOperation> Operations => LazyOperations.Value;
}
