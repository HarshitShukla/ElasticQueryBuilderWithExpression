using Newtonsoft.Json;
using ServiceStack.Text;
using ElasticHelper.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using ProviderMasterAPI.Common.Utility;

namespace ElasticHelper
{
    public static class ElasticExpressionHelper
    {
        private static List<ElasticExpressionPropertyInfo> GetProperties(Type T, string Parameter)
        {
            List<ElasticExpressionPropertyInfo> Props = new List<ElasticExpressionPropertyInfo>();
            try
            {
                T.GetProperties()
                 .ToList()
                 .ForEach(x =>
                 {
                     JsonPropertyAttribute attr2 = x.GetCustomAttributes(true).Where(y => y is JsonPropertyAttribute).FirstOrDefault() as JsonPropertyAttribute;
                     if (attr2 != null)
                     {
                         if (!string.IsNullOrEmpty(attr2.PropertyName))
                            attr2.PropertyName = attr2.PropertyName.Replace("-raw", "");
                         Props.Add(new ElasticExpressionPropertyInfo()
                         {
                             PropertyName = Parameter + "." + x.Name,
                             ElasticPropertyName = attr2.PropertyName,
                             PropertyType = x.PropertyType
                         });
                     }
                     else
                         Props.Add(new ElasticExpressionPropertyInfo()
                         {
                             PropertyName = Parameter + "." + x.Name,
                             ElasticPropertyName = x.Name.Length > 1 ? x.Name.Substring(0, 2).ToLower() + x.Name.Substring(2) : x.Name.ToLower(),
                             PropertyType = x.PropertyType
                         });
                 });
            }
            catch (Exception )
            {
            }

            return Props;
        }

        private static void WalkExpression(List<ElasticExpressionPrecedence> Precedence, int level, Expression expression)
        {
            try
            {
                level += 1;
                ElasticExpressionPrecedence obj = new ElasticExpressionPrecedence()
                {
                    IsParent = (level == 1),
                    Level = level,
                    expressionType = expression.NodeType,
                    Operator = expression.NodeType.ToString(),
                    ElementType = expression.Type
                };

                switch (expression.NodeType)
                {
                    case ExpressionType.MemberAccess:
                        obj.LeftPrecedenceString = expression.ToString();
                        try
                        {
                            var dynValue = Expression.Lambda(expression).Compile().DynamicInvoke();
                            obj.LeftPrecedenceString = dynValue == null ? null : (dynValue is ICollection ? string.Join(",", dynValue.ToJson().FromJson<List<string>>().ToArray()) : dynValue.ToString());
                            if (expression.Type == typeof(Guid) && !string.IsNullOrEmpty(obj.LeftPrecedenceString))
                                obj.LeftPrecedenceString = obj.LeftPrecedenceString.Replace("-", "");
                        }
                        catch { }
                        obj.HasValue = true;
                        break;
                    case ExpressionType.GreaterThan:
                    case ExpressionType.GreaterThanOrEqual:
                    case ExpressionType.LessThan:
                    case ExpressionType.LessThanOrEqual:
                    case ExpressionType.OrElse:
                    case ExpressionType.AndAlso:
                    case ExpressionType.Equal:
                        var bexp = expression as BinaryExpression;
                        if (obj.LeftPrecedence == null)
                            obj.LeftPrecedence = new List<ElasticExpressionPrecedence>();
                        if (obj.RightPrecedence == null)
                            obj.RightPrecedence = new List<ElasticExpressionPrecedence>();
                        WalkExpression(obj.LeftPrecedence, level, bexp.Left);
                        WalkExpression(obj.RightPrecedence, level, bexp.Right);
                        break;
                    case ExpressionType.Call:
                        var methodCall = expression as MethodCallExpression;
                        if (obj.LeftPrecedence == null)
                            obj.LeftPrecedence = new List<ElasticExpressionPrecedence>();

                        int i = 0;
                        foreach (var argument in methodCall.Arguments)
                        {
                            if (i == 1)
                                break;
                            i++;
                            Expression collectionExpression = null;
                            MemberExpression memberExpression = null;
                            if (methodCall != null)
                            {
                                if (methodCall.Type == typeof(DateTime))
                                {
                                    obj.Operator = methodCall.Method.Name;
                                    try
                                    {
                                        var dynValue = Expression.Lambda(expression).Compile().DynamicInvoke();
                                        obj.LeftPrecedenceString = dynValue == null ? null : dynValue.ToString();
                                    }
                                    catch { }
                                    obj.HasValue = true;
                                }
                                else if (methodCall.Method.Name.ToUpper() == "IN")
                                {
                                    if (methodCall.Method.DeclaringType == typeof(ServiceStack.OrmLite.Sql))
                                    {
                                        memberExpression = argument as MemberExpression;
                                        collectionExpression = methodCall.Arguments[1];
                                    }
                                    if (collectionExpression != null && memberExpression != null)
                                    {
                                        var lambda = Expression.Lambda<Func<object>>(collectionExpression, new ParameterExpression[0]);
                                        try
                                        {
                                            var value = lambda.Compile()();
                                            WalkExpression(obj.LeftPrecedence, level, argument);
                                            if (argument != memberExpression)
                                            {
                                                obj.LeftPrecedence.First().LeftPrecedenceString = memberExpression.ToString();
                                            }
                                            obj.Operator = methodCall.Method.Name;
                                            obj.RightPrecedence = new List<ElasticExpressionPrecedence>(){
                                                new ElasticExpressionPrecedence(){
                                                    IsParent = (level+1 == 1), 
                                                    Level = level +1,
                                                    ElementType = typeof(string),
                                                    expressionType = ExpressionType.MemberAccess, 
                                                    Operator = ExpressionType.MemberAccess.ToString(),
                                                    LeftPrecedenceString = 
                                                        ((value as System.Collections.ICollection).OfType<object>().ToArray()[0] as System.Collections.ICollection).IsNotNull() ? 
                                                            string.Join(",",((value as System.Collections.ICollection).OfType<object>().ToArray()[0] as System.Collections.ICollection).OfType<object>().ToArray()).Replace("-","") :
                                                            string.Join(",",(value as System.Collections.ICollection).OfType<object>().ToArray()[0]).Replace("-","")
                                                }
                                            };
                                        }
                                        catch
                                        {
                                            WalkExpression(obj.LeftPrecedence, level, collectionExpression);
                                            obj.Operator = methodCall.Method.Name;
                                            obj.RightPrecedence = new List<ElasticExpressionPrecedence>();
                                            WalkExpression(obj.RightPrecedence, level, memberExpression);
                                        }
                                    }
                                }
                                else if (methodCall.Method.Name == "Contains")
                                {
                                    if (methodCall.Method.DeclaringType == typeof(Enumerable))
                                    {
                                        collectionExpression = argument;
                                        memberExpression = methodCall.Arguments[1] as MemberExpression;
                                    }
                                    else if (methodCall.Method.DeclaringType == typeof(ServiceStack.OrmLite.Sql))
                                    {
                                        memberExpression = argument as MemberExpression;
                                        collectionExpression = methodCall.Arguments[1];
                                    }
                                    else
                                    {
                                        collectionExpression = methodCall.Object;
                                        memberExpression = argument as MemberExpression;
                                    }
                                    if (collectionExpression != null && memberExpression != null)
                                    {
                                        var lambda = Expression.Lambda<Func<object>>(collectionExpression, new ParameterExpression[0]);
                                        try
                                        {
                                            var value = lambda.Compile()();
                                            WalkExpression(obj.LeftPrecedence, level, argument);
                                            if (argument != memberExpression)
                                            {
                                                obj.LeftPrecedence.First().LeftPrecedenceString = memberExpression.ToString();
                                            }
                                            obj.Operator = methodCall.Method.Name;
                                            obj.RightPrecedence = new List<ElasticExpressionPrecedence>(){
                                                new ElasticExpressionPrecedence(){
                                                    IsParent = (level+1 == 1), 
                                                    Level = level +1,
                                                    ElementType = typeof(string),
                                                    expressionType = ExpressionType.MemberAccess, 
                                                    Operator = ExpressionType.MemberAccess.ToString(),
                                                    LeftPrecedenceString = obj.LeftPrecedence[0].ElementType == typeof(string) ? value.ToString() : string.Join(",", (value as System.Collections.ICollection).OfType<object>().ToArray()).Replace("-","")
                                                }
                                            };
                                        }
                                        catch
                                        {
                                            WalkExpression(obj.LeftPrecedence, level, collectionExpression);
                                            obj.Operator = methodCall.Method.Name;
                                            obj.RightPrecedence = new List<ElasticExpressionPrecedence>();
                                            WalkExpression(obj.RightPrecedence, level, memberExpression);
                                        }
                                    }
                                    else
                                    {
                                        if (memberExpression == null)
                                        {
                                            obj.Operator = methodCall.Method.Name;
                                            obj.LeftPrecedence.Add(new ElasticExpressionPrecedence()
                                            {
                                                IsParent = (level + 1 == 1),
                                                Level = level + 1,
                                                ElementType = typeof(string),
                                                expressionType = ExpressionType.MemberAccess,
                                                Operator = ExpressionType.MemberAccess.ToString(),
                                                LeftPrecedenceString = collectionExpression.ToString()
                                            });
                                            obj.RightPrecedence = new List<ElasticExpressionPrecedence>(){
                                            new ElasticExpressionPrecedence(){
                                                IsParent = (level+1 == 1), 
                                                Level = level +1,
                                                ElementType = typeof(string),
                                                expressionType = ExpressionType.MemberAccess, 
                                                Operator = ExpressionType.MemberAccess.ToString(),
                                                LeftPrecedenceString = argument.ToString().Replace("\"","")
                                            }
                                        };
                                        }
                                    }
                                }
                            }
                        }
                        break;
                    case ExpressionType.Not:
                        var notexp = expression as UnaryExpression;
                        if (obj.LeftPrecedence == null)
                            obj.LeftPrecedence = new List<ElasticExpressionPrecedence>();
                        WalkExpression(obj.LeftPrecedence, level, notexp.Operand);
                        break;
                    case ExpressionType.Lambda:
                        var lexp = expression as LambdaExpression;
                        if (obj.LeftPrecedence == null)
                            obj.LeftPrecedence = new List<ElasticExpressionPrecedence>();
                        WalkExpression(obj.LeftPrecedence, level, lexp.Body);
                        break;
                    case ExpressionType.Constant:
                        obj.LeftPrecedenceString = expression.ToString();
                        break;
                    default:
                        break;
                }
                Precedence.Add(obj);
            }
            catch
            {
            }
        }

        private static List<ElasticExpressionPrecedence> GetExpressionPrecedence<T>(Expression<Func<T, bool>> expression)
        {
            List<ElasticExpressionPrecedence> expressionPrecedence = new List<ElasticExpressionPrecedence>();
            try
            {
                int level = 0;
                WalkExpression(expressionPrecedence, level, expression);
            }
            catch
            {
            }
            return expressionPrecedence;
        }

        private static string ElasticFilterPostExpression(List<ElasticExpressionPrecedence> expressionPrecedence, List<ElasticExpressionPropertyInfo> PropertiesInfo, bool ConvertDateTimeToLong = false)
        {
            string Expression = string.Empty, LeftExpressionValue = string.Empty, RightExpressionValue = string.Empty;
            try
            {
                switch (expressionPrecedence[0].expressionType)
                {
                    case ExpressionType.MemberAccess:
                        ElasticExpressionPropertyInfo propInfo = PropertiesInfo.FirstOrDefault(x => x.PropertyName == expressionPrecedence[0].LeftPrecedenceString);
                        Expression = "\"" + (propInfo != null ? propInfo.ElasticPropertyName : expressionPrecedence[0].LeftPrecedenceString) + "\"";
                        break;
                    case ExpressionType.GreaterThan:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        RightExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].RightPrecedence, PropertiesInfo, ConvertDateTimeToLong).Replace("\"", "");
                        if (ConvertDateTimeToLong && expressionPrecedence[0].LeftPrecedence[0].ElementType == typeof(DateTime))
                            RightExpressionValue = DateTime.Parse(RightExpressionValue).ToElasticDate().ToString();
                        Expression = "{\"range\":{" + LeftExpressionValue + ":{\"gt\":" + RightExpressionValue + "}}}";
                        break;
                    case ExpressionType.GreaterThanOrEqual:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        RightExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].RightPrecedence, PropertiesInfo, ConvertDateTimeToLong).Replace("\"", "");
                        if (ConvertDateTimeToLong && expressionPrecedence[0].LeftPrecedence[0].ElementType == typeof(DateTime))
                            RightExpressionValue = DateTime.Parse(RightExpressionValue).ToElasticDate().ToString();
                        Expression = "{\"range\":{" + LeftExpressionValue + ":{\"gte\":" + RightExpressionValue + "}}}";
                        break;
                    case ExpressionType.LessThan:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        RightExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].RightPrecedence, PropertiesInfo, ConvertDateTimeToLong).Replace("\"", "");
                        if (ConvertDateTimeToLong && expressionPrecedence[0].LeftPrecedence[0].ElementType == typeof(DateTime))
                            RightExpressionValue = DateTime.Parse(RightExpressionValue).ToElasticDate().ToString();
                        Expression = "{\"range\":{" + LeftExpressionValue + ":{\"lt\":" + RightExpressionValue + "}}}";
                        break;
                    case ExpressionType.LessThanOrEqual:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        RightExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].RightPrecedence, PropertiesInfo, ConvertDateTimeToLong).Replace("\"", "");
                        if (ConvertDateTimeToLong && expressionPrecedence[0].LeftPrecedence[0].ElementType == typeof(DateTime))
                            RightExpressionValue = DateTime.Parse(RightExpressionValue).ToElasticDate().ToString();
                        Expression = "{\"range\":{" + LeftExpressionValue + ":{\"lte\":" + RightExpressionValue + "}}}";
                        break;
                    case ExpressionType.OrElse:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        if (!LeftExpressionValue.Contains("{"))
                        {
                            var subExpressionProperty = expressionPrecedence[0].LeftPrecedence[0];
                            if (subExpressionProperty.expressionType == ExpressionType.MemberAccess &&
                                (subExpressionProperty.ElementType == typeof(bool) || subExpressionProperty.ElementType == typeof(Boolean)))
                            {
                                LeftExpressionValue = "{\"term\":{" + LeftExpressionValue + ":true}}";
                            }
                        }
                        RightExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].RightPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        if (!RightExpressionValue.Contains("{"))
                        {
                            var subExpressionProperty = expressionPrecedence[0].RightPrecedence[0];
                            if (subExpressionProperty.expressionType == ExpressionType.MemberAccess &&
                                (subExpressionProperty.ElementType == typeof(bool) || subExpressionProperty.ElementType == typeof(Boolean)))
                            {
                                RightExpressionValue = "{\"term\":{" + RightExpressionValue + ":true}}";
                            }
                        }
                        Expression = "{\"bool\":{\"should\":[" + LeftExpressionValue + (string.IsNullOrEmpty(RightExpressionValue) ? string.Empty : ",") + RightExpressionValue + "]}}";
                        break;
                    case ExpressionType.AndAlso:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        if (!LeftExpressionValue.Contains("{"))
                        {
                            var subExpressionProperty = expressionPrecedence[0].LeftPrecedence[0];
                            if (subExpressionProperty.expressionType == ExpressionType.MemberAccess &&
                                (subExpressionProperty.ElementType == typeof(bool) || subExpressionProperty.ElementType == typeof(Boolean)))
                            {
                                LeftExpressionValue = "{\"term\":{" + LeftExpressionValue + ":true}}";
                            }
                        }
                        RightExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].RightPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        if (!RightExpressionValue.Contains("{"))
                        {
                            var subExpressionProperty = expressionPrecedence[0].RightPrecedence[0];
                            if (subExpressionProperty.expressionType == ExpressionType.MemberAccess &&
                                (subExpressionProperty.ElementType == typeof(bool) || subExpressionProperty.ElementType == typeof(Boolean)))
                            {
                                RightExpressionValue = "{\"term\":{" + RightExpressionValue + ":true}}";
                            }
                        }
                        Expression = "{\"bool\":{\"must\":[" + LeftExpressionValue + (string.IsNullOrEmpty(RightExpressionValue) ? string.Empty : ",") + RightExpressionValue + "]}}";
                        break;
                    case ExpressionType.Equal:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        RightExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].RightPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        Expression = (expressionPrecedence[0].LeftPrecedence[0].ElementType != typeof(string) ? "{\"term\":{" : "{\"match_phrase\":{") + LeftExpressionValue + ":" + RightExpressionValue + "}}";
                        break;
                    case ExpressionType.Call:
                        if (expressionPrecedence[0].Operator == "Parse")
                            Expression = expressionPrecedence[0].LeftPrecedenceString;
                        else
                        {
                            LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                            RightExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].RightPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                            if (expressionPrecedence[0].Operator == "Contains" || expressionPrecedence[0].Operator.ToUpper() == "IN")
                            {
                                if (expressionPrecedence[0].LeftPrecedence[0].ElementType == typeof(string[]) ||
                                    expressionPrecedence[0].LeftPrecedence[0].ElementType == typeof(List<string>))
                                {
                                    List<string> ArrayExpression = new List<string>();
                                    RightExpressionValue.Replace("\"", "").Split(new[] { ',' }).ToList().ForEach(value =>
                                        ArrayExpression.Add("{\"match_phrase\":{" + LeftExpressionValue + ":\"" + value + "\"}}")
                                    );
                                    Expression = "{\"bool\": {\"should\": [" + string.Join(",", ArrayExpression) + "]}}";
                                }
                                else if (expressionPrecedence[0].LeftPrecedence[0].ElementType != typeof(string))
                                {
                                    RightExpressionValue = string.Join(",", RightExpressionValue.Replace("\"", "").Split(new[] { ',' }).ToList().Select(x => "\"" + x + "\"").ToArray());
                                    Expression = "{\"terms\":{" + LeftExpressionValue + ":[" + RightExpressionValue + "]}}";
                                }
                                else
                                    Expression = "{\"match_phrase\":{" + LeftExpressionValue + ":" + RightExpressionValue + "}}";
                            }
                        }
                        break;
                    case ExpressionType.Not:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        if (!LeftExpressionValue.Contains("{"))
                        {
                            var subExpressionProperty = expressionPrecedence[0].LeftPrecedence[0];
                            if (subExpressionProperty.expressionType == ExpressionType.MemberAccess &&
                                (subExpressionProperty.ElementType == typeof(bool) || subExpressionProperty.ElementType == typeof(Boolean)))
                            {
                                LeftExpressionValue = "{\"term\":{" + LeftExpressionValue + ":true}}";
                            }
                        }
                        Expression = "{\"bool\":{\"must_not\":[" + LeftExpressionValue + "]}}";
                        break;
                    case ExpressionType.Lambda:
                        LeftExpressionValue = ElasticFilterPostExpression(expressionPrecedence[0].LeftPrecedence, PropertiesInfo, ConvertDateTimeToLong);
                        if (!LeftExpressionValue.Contains("{"))
                        {
                            var subExpressionProperty = expressionPrecedence[0].LeftPrecedence[0];
                            if (subExpressionProperty.expressionType == ExpressionType.MemberAccess &&
                                (subExpressionProperty.ElementType == typeof(bool) || subExpressionProperty.ElementType == typeof(Boolean)))
                            {
                                LeftExpressionValue = "{\"term\":{" + LeftExpressionValue + ":true}}";
                            }
                        }
                        Expression = LeftExpressionValue;
                        break;
                    case ExpressionType.Constant:
                        Expression = expressionPrecedence[0].LeftPrecedenceString.Contains("\"") ? expressionPrecedence[0].LeftPrecedenceString : "\"" + expressionPrecedence[0].LeftPrecedenceString + "\"";
                        break;
                    default:
                        break;
                }
            }
            catch
            {
            }
            return Expression;
        }

        public static string LambdaToString<T>(Expression<Func<T, bool>> expression, long from = 0, long size = 10000, List<string> SelectFields = null)
        {
            List<ElasticExpressionPropertyInfo> PropertiesInfo = GetProperties(typeof(T), expression.Parameters[0].ToString());
            List<string> ElasticFields = !SelectFields.HasRecords() ? null : PropertiesInfo.Where(x => SelectFields.Any(y => y.ToUpper() == x.GetPropertyNameWithouAlias().ToUpper())).Select(x => x.ElasticPropertyName).ToList();

            var expressionPrecedence = GetExpressionPrecedence(expression);

            string FilterExpression = ElasticFilterPostExpression(expressionPrecedence, PropertiesInfo, true);
            string QueryExpression = "\"query\":{\"constant_score\":{\"filter\":" + FilterExpression + "}}";
            if (!ElasticFields.HasRecords())
                ElasticFields = new List<string>();
            string PostExpression = "{\"_source\":" + ElasticFields.ToJson() + ",\"from\":" + from.ToString() + ",\"size\":" + size.ToString() + "," + QueryExpression + "}";
            return PostExpression;
        }
    }

    public class ElasticExpressionPropertyInfo
    {
        public string PropertyName { get; set; }
        public string ElasticPropertyName { get; set; }
        public Type PropertyType { get; set; }
        public string GetPropertyNameWithouAlias()
        {
            if (string.IsNullOrEmpty(this.PropertyName))
                return this.PropertyName;

            var SplittedString = this.PropertyName.Split('.');
            return SplittedString.HasRecords() && SplittedString.Length > 1 ? SplittedString[1] : SplittedString[0];
        }
    }

    public class ElasticExpressionPrecedence
    {
        public string Operator { get; set; }
        public int Level { get; set; }
        public bool IsParent { get; set; }
        public bool HasValue { get; set; }
        public Type ElementType { get; set; }
        public ExpressionType expressionType { get; set; }
        public string LeftPrecedenceString { get; set; }
        public string RightPrecedenceString { get; set; }
        public List<ElasticExpressionPrecedence> LeftPrecedence { get; set; }
        public List<ElasticExpressionPrecedence> RightPrecedence { get; set; }
    }

    public class AndAlsoModifier : ExpressionVisitor
    {
        public Expression Modify(Expression expression)
        {
            return Visit(expression);
        }

        protected override Expression VisitBinary(BinaryExpression b)
        {
            if (b.NodeType == ExpressionType.AndAlso)
            {
                Expression left = this.Visit(b.Left);
                Expression right = this.Visit(b.Right);

                // Make this binary expression an OrElse operation instead of an AndAlso operation.  
                return Expression.MakeBinary(ExpressionType.OrElse, left, right, b.IsLiftedToNull, b.Method);
            }

            return base.VisitBinary(b);
        }
    }  
}
