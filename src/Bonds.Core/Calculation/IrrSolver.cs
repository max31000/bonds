namespace Bonds.Core.Calculation;

/// <summary>
/// Универсальный решатель IRR (внутренней нормы доходности): находит ставку <c>r</c>,
/// при которой <c>f(r) = 0</c>, где f — функция чистой приведённой стоимости денежного потока.
/// Метод — Ньютон-Рафсон с гарантированным фолбэком на бисекцию (spec §6.3, §6.9; plan/05
/// Часть A.3/A.8 — "гарантия сходимости — это не опция"). Используется и YTM, и XIRR,
/// поэтому вынесен в общий класс. Чистая функция, без I/O, без побочных эффектов.
/// </summary>
public static class IrrSolver
{
    public const int MaxNewtonIterations = 100;
    public const int MaxBisectionIterations = 200;
    public const decimal DefaultTolerance = 1e-10m;

    public readonly record struct Result(decimal Rate, bool Converged, bool UsedBisectionFallback, int Iterations);

    /// <summary>
    /// Решает f(r) = 0 методом Ньютона-Рафсона; если он не сходится (расходится, выходит за
    /// допустимые границы ставки, производная вырождается, либо превышен лимит итераций),
    /// автоматически переключается на бисекцию на широком интервале ставок.
    /// </summary>
    /// <param name="f">Функция NPV(r) — должна быть монотонной/иметь единственный разумный корень
    /// для денежных потоков с одной сменой знака (типичный случай бумаги/портфеля).</param>
    /// <param name="fPrime">Аналитическая производная NPV'(r) для шага Ньютона.</param>
    /// <param name="initialGuess">Начальное приближение ставки.</param>
    /// <param name="lowerBound">Нижняя граница для бисекции (по умолчанию -0.99, т.е. -99%).</param>
    /// <param name="upperBound">Верхняя граница для бисекции (по умолчанию 10.0, т.е. 1000%).</param>
    /// <param name="tolerance">Допуск по |f(r)| и по ширине интервала бисекции.</param>
    public static Result Solve(
        Func<decimal, decimal> f,
        Func<decimal, decimal> fPrime,
        decimal initialGuess,
        decimal lowerBound = -0.99m,
        decimal upperBound = 10.0m,
        decimal tolerance = DefaultTolerance)
    {
        var newton = TryNewtonRaphson(f, fPrime, initialGuess, lowerBound, upperBound, tolerance);
        if (newton.Converged)
        {
            return newton;
        }

        var bisection = TryBisection(f, lowerBound, upperBound, tolerance);
        return bisection with { Iterations = newton.Iterations + bisection.Iterations };
    }

    private static Result TryNewtonRaphson(
        Func<decimal, decimal> f,
        Func<decimal, decimal> fPrime,
        decimal initialGuess,
        decimal lowerBound,
        decimal upperBound,
        decimal tolerance)
    {
        var rate = initialGuess;

        for (var i = 0; i < MaxNewtonIterations; i++)
        {
            decimal value;
            decimal derivative;
            try
            {
                value = f(rate);
                derivative = fPrime(rate);
            }
            catch (OverflowException)
            {
                return new Result(rate, false, false, i);
            }

            if (Math.Abs(value) < tolerance)
            {
                return new Result(rate, true, false, i);
            }

            if (derivative == 0m)
            {
                return new Result(rate, false, false, i);
            }

            var nextRate = rate - value / derivative;

            // Производная вырождается / шаг "взрывается" / выходит далеко за разумные границы —
            // отдаём управление бисекции, а не гоняемся за NaN/overflow.
            if (nextRate <= lowerBound || nextRate >= upperBound || !IsFinite(nextRate))
            {
                return new Result(rate, false, false, i);
            }

            if (Math.Abs(nextRate - rate) < tolerance)
            {
                return new Result(nextRate, true, false, i + 1);
            }

            rate = nextRate;
        }

        return new Result(rate, false, false, MaxNewtonIterations);
    }

    private static Result TryBisection(
        Func<decimal, decimal> f,
        decimal lowerBound,
        decimal upperBound,
        decimal tolerance)
    {
        var lo = lowerBound;
        var hi = upperBound;

        decimal fLo;
        decimal fHi;
        try
        {
            fLo = f(lo);
            fHi = f(hi);
        }
        catch (OverflowException)
        {
            return new Result(0m, false, true, 0);
        }

        // Бисекция требует разных знаков на концах интервала. Если знаки совпадают,
        // последовательно сужаем/сканируем интервал в поисках смены знака.
        if (Math.Sign(fLo) == Math.Sign(fHi))
        {
            var found = false;
            const int scanSteps = 400;
            var step = (hi - lo) / scanSteps;
            var prevX = lo;
            var prevF = fLo;

            for (var i = 1; i <= scanSteps; i++)
            {
                var x = lo + step * i;
                decimal fx;
                try
                {
                    fx = f(x);
                }
                catch (OverflowException)
                {
                    continue;
                }

                if (Math.Sign(fx) != Math.Sign(prevF) || fx == 0m)
                {
                    lo = prevX;
                    hi = x;
                    fLo = prevF;
                    fHi = fx;
                    found = true;
                    break;
                }

                prevX = x;
                prevF = fx;
            }

            if (!found)
            {
                return new Result(0m, false, true, scanSteps);
            }
        }

        for (var i = 0; i < MaxBisectionIterations; i++)
        {
            var mid = (lo + hi) / 2m;
            var fMid = f(mid);

            if (Math.Abs(fMid) < tolerance || (hi - lo) < tolerance)
            {
                return new Result(mid, true, true, i);
            }

            if (Math.Sign(fMid) == Math.Sign(fLo))
            {
                lo = mid;
                fLo = fMid;
            }
            else
            {
                hi = mid;
                fHi = fMid;
            }
        }

        return new Result((lo + hi) / 2m, true, true, MaxBisectionIterations);
    }

    private static bool IsFinite(decimal value) => true; // decimal в .NET не имеет NaN/Infinity по конструкции
}
